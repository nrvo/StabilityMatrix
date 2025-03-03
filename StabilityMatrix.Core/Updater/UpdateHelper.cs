﻿using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models.Configs;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Models.Update;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Core.Updater;

[Singleton(typeof(IUpdateHelper))]
public class UpdateHelper : IUpdateHelper
{
    private readonly ILogger<UpdateHelper> logger;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IDownloadService downloadService;
    private readonly ISettingsManager settingsManager;
    private readonly DebugOptions debugOptions;
    private readonly System.Timers.Timer timer = new(TimeSpan.FromMinutes(60));

    private string UpdateManifestUrl =>
        debugOptions.UpdateManifestUrl ?? "https://cdn.lykos.ai/update-v3.json";

    public const string UpdateFolderName = ".StabilityMatrixUpdate";
    public static DirectoryPath UpdateFolder => Compat.AppCurrentDir.JoinDir(UpdateFolderName);

    public static FilePath ExecutablePath => UpdateFolder.JoinFile(Compat.GetExecutableName());

    public UpdateHelper(
        ILogger<UpdateHelper> logger,
        IHttpClientFactory httpClientFactory,
        IDownloadService downloadService,
        IOptions<DebugOptions> debugOptions,
        ISettingsManager settingsManager
    )
    {
        this.logger = logger;
        this.httpClientFactory = httpClientFactory;
        this.downloadService = downloadService;
        this.settingsManager = settingsManager;
        this.debugOptions = debugOptions.Value;

        timer.Elapsed += async (_, _) =>
        {
            await CheckForUpdate().ConfigureAwait(false);
        };
    }

    public async Task StartCheckingForUpdates()
    {
        timer.Enabled = true;
        timer.Start();
        await CheckForUpdate().ConfigureAwait(false);
    }

    public async Task DownloadUpdate(UpdateInfo updateInfo, IProgress<ProgressReport> progress)
    {
        UpdateFolder.Create();
        UpdateFolder.Info.Attributes |= FileAttributes.Hidden;

        var downloadFile = UpdateFolder.JoinFile(Path.GetFileName(updateInfo.Url.ToString()));

        var extractDir = UpdateFolder.JoinDir("extract");

        try
        {
            // download the file from URL
            await downloadService
                .DownloadToFileAsync(
                    updateInfo.Url.ToString(),
                    downloadFile,
                    progress: progress,
                    httpClientName: "UpdateClient"
                )
                .ConfigureAwait(false);

            // Unzip if needed
            if (downloadFile.Extension == ".zip")
            {
                if (extractDir.Exists)
                {
                    await extractDir.DeleteAsync(true).ConfigureAwait(false);
                }
                extractDir.Create();

                progress.Report(
                    new ProgressReport(-1, isIndeterminate: true, type: ProgressType.Extract)
                );
                await ArchiveHelper.Extract(downloadFile, extractDir).ConfigureAwait(false);

                // Find binary and move it up to the root
                var binaryFile = extractDir
                    .EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .First(f => f.Extension.ToLowerInvariant() is ".exe" or ".appimage");

                await binaryFile.MoveToAsync(ExecutablePath).ConfigureAwait(false);
            }
            // Otherwise just rename
            else
            {
                downloadFile.Rename(ExecutablePath.Name);
            }

            progress.Report(new ProgressReport(1d));
        }
        finally
        {
            // Clean up original download
            await downloadFile.DeleteAsync().ConfigureAwait(false);
            // Clean up extract dir
            if (extractDir.Exists)
            {
                await extractDir.DeleteAsync(true).ConfigureAwait(false);
            }
        }
    }

    private async Task CheckForUpdate()
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient("UpdateClient");
            var response = await httpClient.GetAsync(UpdateManifestUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Error while checking for update {StatusCode} - {Content}",
                    response.StatusCode,
                    await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                );
                return;
            }

            var updateManifest = await JsonSerializer
                .DeserializeAsync<UpdateManifest>(
                    await response.Content.ReadAsStreamAsync().ConfigureAwait(false),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
                )
                .ConfigureAwait(false);

            if (updateManifest is null)
            {
                logger.LogError("UpdateManifest is null");
                return;
            }

            foreach (
                var channel in Enum.GetValues(typeof(UpdateChannel))
                    .Cast<UpdateChannel>()
                    .Where(
                        c =>
                            c > UpdateChannel.Unknown
                            && c <= settingsManager.Settings.PreferredUpdateChannel
                    )
            )
            {
                if (
                    updateManifest.Updates.TryGetValue(channel, out var platforms)
                    && platforms.GetInfoForCurrentPlatform() is { } update
                    && ValidateUpdate(update)
                )
                {
                    NotifyUpdateAvailable(update);
                    return;
                }
            }

            logger.LogInformation("No update available");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Couldn't check for update");
        }
    }

    private bool ValidateUpdate(UpdateInfo? update)
    {
        if (update is null)
            return false;

        // Verify signature
        var checker = new SignatureChecker();
        var signedData = update.GetSignedData();

        if (!checker.Verify(signedData, update.Signature))
        {
            logger.LogError(
                "UpdateInfo signature {Signature} is invalid, Data = {Data}, UpdateInfo = {Info}",
                update.Signature,
                signedData,
                update
            );
            return false;
        }

        switch (update.Version.ComparePrecedenceTo(Compat.AppVersion))
        {
            case > 0:
                // Newer version available
                return true;
            case 0:
            {
                // Same version available, check if we both have commit hash metadata
                var updateHash = update.Version.Metadata;
                var appHash = Compat.AppVersion.Metadata;

                // Trim both to the lower length, to a minimum of 7 characters
                var minLength = Math.Min(7, Math.Min(updateHash.Length, appHash.Length));
                updateHash = updateHash[..minLength];
                appHash = appHash[..minLength];

                // If different, we can update
                if (updateHash != appHash)
                {
                    return true;
                }

                break;
            }
        }

        return false;
    }

    private void NotifyUpdateAvailable(UpdateInfo update)
    {
        logger.LogInformation(
            "Update available {AppVer} -> {UpdateVer}",
            Compat.AppVersion,
            update.Version
        );
        EventManager.Instance.OnUpdateAvailable(update);
    }
}

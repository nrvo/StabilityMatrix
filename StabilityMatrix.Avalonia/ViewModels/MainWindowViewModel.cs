﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using NLog;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.ViewModels.Progress;
using StabilityMatrix.Avalonia.Views;
using StabilityMatrix.Avalonia.Views.Dialogs;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models.Update;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels;

[View(typeof(MainWindow))]
public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ISettingsManager settingsManager;
    private readonly ServiceManager<ViewModelBase> dialogFactory;
    private readonly ITrackedDownloadService trackedDownloadService;
    private readonly IDiscordRichPresenceService discordRichPresenceService;
    private readonly IModelIndexService modelIndexService;
    public string Greeting => "Welcome to Avalonia!";

    [ObservableProperty]
    private PageViewModelBase? currentPage;

    [ObservableProperty]
    private object? selectedCategory;

    [ObservableProperty]
    private List<PageViewModelBase> pages = new();

    [ObservableProperty]
    private List<PageViewModelBase> footerPages = new();

    public ProgressManagerViewModel ProgressManagerViewModel { get; init; }
    public UpdateViewModel UpdateViewModel { get; init; }

    public MainWindowViewModel(
        ISettingsManager settingsManager,
        IDiscordRichPresenceService discordRichPresenceService,
        ServiceManager<ViewModelBase> dialogFactory,
        ITrackedDownloadService trackedDownloadService,
        IModelIndexService modelIndexService
    )
    {
        this.settingsManager = settingsManager;
        this.dialogFactory = dialogFactory;
        this.discordRichPresenceService = discordRichPresenceService;
        this.trackedDownloadService = trackedDownloadService;
        this.modelIndexService = modelIndexService;
        ProgressManagerViewModel = dialogFactory.Get<ProgressManagerViewModel>();
        UpdateViewModel = dialogFactory.Get<UpdateViewModel>();
    }

    public override void OnLoaded()
    {
        base.OnLoaded();

        // Set only if null, since this may be called again when content dialogs open
        CurrentPage ??= Pages.FirstOrDefault();
        SelectedCategory ??= Pages.FirstOrDefault();
    }

    public override async Task OnLoadedAsync()
    {
        await base.OnLoadedAsync();

        // Skip if design mode
        if (Design.IsDesignMode)
            return;

        if (!await EnsureDataDirectory())
        {
            // False if user exited dialog, shutdown app
            App.Shutdown();
            return;
        }

        // Initialize Discord Rich Presence (this needs LibraryDir so is set here)
        discordRichPresenceService.UpdateState();

        // Load in-progress downloads
        ProgressManagerViewModel.AddDownloads(trackedDownloadService.Downloads);

        // Index checkpoints if we dont have
        Task.Run(() => settingsManager.IndexCheckpoints()).SafeFireAndForget();

        if (!App.IsHeadlessMode)
        {
            PreloadPages();
        }

        Program.StartupTimer.Stop();
        var startupTime = CodeTimer.FormatTime(Program.StartupTimer.Elapsed);
        Logger.Info($"App started ({startupTime})");

        if (Program.Args.DebugOneClickInstall || !settingsManager.Settings.InstalledPackages.Any())
        {
            var viewModel = dialogFactory.Get<OneClickInstallViewModel>();
            var dialog = new BetterContentDialog
            {
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false,
                IsFooterVisible = false,
                Content = new OneClickInstallDialog { DataContext = viewModel },
            };

            EventManager.Instance.OneClickInstallFinished += (_, skipped) =>
            {
                dialog.Hide();
                if (skipped)
                    return;

                EventManager.Instance.OnTeachingTooltipNeeded();
            };

            await dialog.ShowAsync(App.TopLevel);
        }
    }

    private void PreloadPages()
    {
        // Preload pages with Preload attribute
        foreach (
            var page in Pages
                .Concat(FooterPages)
                .Where(p => p.GetType().GetCustomAttributes(typeof(PreloadAttribute), true).Any())
        )
        {
            Dispatcher.UIThread
                .InvokeAsync(
                    async () =>
                    {
                        var stopwatch = Stopwatch.StartNew();

                        // ReSharper disable once MethodHasAsyncOverload
                        page.OnLoaded();
                        await page.OnLoadedAsync();

                        // Get view
                        new ViewLocator().Build(page);

                        Logger.Trace(
                            $"Preloaded page {page.GetType().Name} in {stopwatch.Elapsed.TotalMilliseconds:F1}ms"
                        );
                    },
                    DispatcherPriority.Background
                )
                .ContinueWith(task =>
                {
                    if (task.Exception is { } exception)
                    {
                        Logger.Error(exception, "Error preloading page");
                        Debug.Fail(exception.Message);
                    }
                });
        }
    }

    /// <summary>
    /// Check if the data directory exists, if not, show the select data directory dialog.
    /// </summary>
    private async Task<bool> EnsureDataDirectory()
    {
        // If we can't find library, show selection dialog
        var foundInitially = settingsManager.TryFindLibrary();
        if (!foundInitially)
        {
            var result = await ShowSelectDataDirectoryDialog();
            if (!result)
                return false;
        }

        // Try to find library again, should be found now
        if (!settingsManager.TryFindLibrary())
        {
            throw new Exception("Could not find library after setting path");
        }

        // Tell LaunchPage to load any packages if they selected an existing directory
        if (!foundInitially)
        {
            EventManager.Instance.OnInstalledPackagesChanged();
        }

        // Check if there are old packages, if so show migration dialog
        // TODO: Migration dialog

        return true;
    }

    /// <summary>
    /// Return true if we should show the update available teaching tip
    /// </summary>
    public bool ShouldShowUpdateAvailableTeachingTip([NotNullWhen(true)] UpdateInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        // If matching settings seen version, don't show
        if (info.Version == settingsManager.Settings.LastSeenUpdateVersion)
        {
            return false;
        }

        // Save that we have dismissed this update
        settingsManager.Transaction(
            s => s.LastSeenUpdateVersion = info.Version,
            ignoreMissingLibraryDir: true
        );

        return true;
    }

    /// <summary>
    /// Shows the select data directory dialog.
    /// </summary>
    /// <returns>true if path set successfully, false if user exited dialog.</returns>
    private async Task<bool> ShowSelectDataDirectoryDialog()
    {
        var viewModel = dialogFactory.Get<SelectDataDirectoryViewModel>();
        var dialog = new BetterContentDialog
        {
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            IsFooterVisible = false,
            Content = new SelectDataDirectoryDialog { DataContext = viewModel }
        };

        var result = await dialog.ShowAsync(App.TopLevel);
        if (result == ContentDialogResult.Primary)
        {
            // 1. For portable mode, call settings.SetPortableMode()
            if (viewModel.IsPortableMode)
            {
                settingsManager.SetPortableMode();
            }
            // 2. For custom path, call settings.SetLibraryPath(path)
            else
            {
                settingsManager.SetLibraryPath(viewModel.DataDirectory);
            }
            // Indicate success
            return true;
        }

        return false;
    }

    public async Task ShowUpdateDialog()
    {
        var viewModel = dialogFactory.Get<UpdateViewModel>();
        var dialog = new BetterContentDialog
        {
            ContentVerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            IsFooterVisible = false,
            Content = new UpdateDialog { DataContext = viewModel }
        };

        await viewModel.Preload();
        await dialog.ShowAsync();
    }
}

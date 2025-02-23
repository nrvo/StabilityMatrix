﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using StabilityMatrix.Avalonia.Animations;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Inference;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Database;
using StabilityMatrix.Core.Models.FileInterfaces;

#pragma warning disable CS0657 // Not a valid attribute location for this declaration

namespace StabilityMatrix.Avalonia.ViewModels.Base;

public abstract partial class InferenceTabViewModelBase
    : LoadableViewModelBase,
        IDisposable,
        IPersistentViewProvider,
        IDropTarget
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notificationService;

    /// <summary>
    /// The title of the tab
    /// </summary>
    public virtual string TabTitle => ProjectFile?.NameWithoutExtension ?? "New Project";

    /// <summary>
    /// Whether there are unsaved changes
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool hasUnsavedChanges;

    /// <summary>
    /// The tab's project file
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    [property: JsonIgnore]
    private FilePath? projectFile;

    /// <inheritdoc />
    Control? IPersistentViewProvider.AttachedPersistentView { get; set; }

    #region Weak Events

    private WeakEventManager<LoadViewStateEventArgs>? loadViewStateRequestedEventManager;

    public event EventHandler<LoadViewStateEventArgs> LoadViewStateRequested
    {
        add
        {
            loadViewStateRequestedEventManager ??= new WeakEventManager<LoadViewStateEventArgs>();
            loadViewStateRequestedEventManager.AddEventHandler(value);
        }
        remove => loadViewStateRequestedEventManager?.RemoveEventHandler(value);
    }

    protected void LoadViewState(LoadViewStateEventArgs args) =>
        loadViewStateRequestedEventManager?.RaiseEvent(this, args, nameof(LoadViewStateRequested));

    protected void ResetViewState() => LoadViewState(new LoadViewStateEventArgs());

    private WeakEventManager<SaveViewStateEventArgs>? saveViewStateRequestedEventManager;

    public event EventHandler<SaveViewStateEventArgs> SaveViewStateRequested
    {
        add
        {
            saveViewStateRequestedEventManager ??= new WeakEventManager<SaveViewStateEventArgs>();
            saveViewStateRequestedEventManager.AddEventHandler(value);
        }
        remove => saveViewStateRequestedEventManager?.RemoveEventHandler(value);
    }

    protected async Task<ViewState> SaveViewState()
    {
        var eventArgs = new SaveViewStateEventArgs();
        saveViewStateRequestedEventManager?.RaiseEvent(
            this,
            eventArgs,
            nameof(SaveViewStateRequested)
        );

        if (eventArgs.StateTask is not { } stateTask)
        {
            throw new InvalidOperationException(
                "SaveViewStateRequested event handler did not set the StateTask property"
            );
        }

        return await stateTask;
    }

    #endregion

    protected InferenceTabViewModelBase(INotificationService notificationService)
    {
        this.notificationService = notificationService;
    }

    [RelayCommand]
    private void RestoreDefaultViewState()
    {
        // ResetViewState();
        // TODO: Dock reset not working, using this hack for now to get a new view

        var navService = App.Services.GetRequiredService<INavigationService>();
        navService.NavigateTo<LaunchPageViewModel>(new SuppressNavigationTransitionInfo());
        ((IPersistentViewProvider)this).AttachedPersistentView = null;
        navService.NavigateTo<InferenceViewModel>(new BetterEntranceNavigationTransition());
    }

    [RelayCommand]
    private async Task DebugSaveViewState()
    {
        var state = await SaveViewState();
        if (state.DockLayout is { } layout)
        {
            await DialogHelper.CreateJsonDialog(layout).ShowAsync();
        }
        else
        {
            await DialogHelper.CreateTaskDialog("Failed", "No layout data").ShowAsync();
        }
    }

    [RelayCommand]
    private async Task DebugLoadViewState()
    {
        var textFields = new TextBoxField[] { new() { Label = "Json Data" } };

        var dialog = DialogHelper.CreateTextEntryDialog("Load Dock State", "", textFields);
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && textFields[0].Text is { } json)
        {
            LoadViewState(
                new LoadViewStateEventArgs { State = new ViewState { DockLayout = json } }
            );
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            ((IPersistentViewProvider)this).AttachedPersistentView = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Loads image and metadata from a file path
    /// </summary>
    /// <remarks>This is safe to call from non-UI threads</remarks>
    /// <param name="filePath">File path</param>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="ApplicationException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    private void LoadImageMetadata(FilePath? filePath)
    {
        if (filePath is not { Exists: true })
        {
            throw new FileNotFoundException("File does not exist", filePath?.FullPath);
        }

        var metadata = ImageMetadata.GetAllFileMetadata(filePath);

        // Has SMProject metadata
        if (metadata.SMProject is not null)
        {
            var project = JsonSerializer.Deserialize<InferenceProjectDocument>(metadata.SMProject);

            // Check project type matches
            if (project?.ProjectType.ToViewModelType() == GetType() && project.State is not null)
            {
                Dispatcher.UIThread.Invoke(() => LoadStateFromJsonObject(project.State));
            }
            else
            {
                throw new ApplicationException("Unsupported project type");
            }

            // Load image
            if (this is IImageGalleryComponent imageGalleryComponent)
            {
                imageGalleryComponent.LoadImagesToGallery(new ImageSource(filePath));
            }
        }
        // Has generic metadata
        else if (metadata.Parameters is { } parametersString)
        {
            if (!GenerationParameters.TryParse(parametersString, out var parameters))
            {
                throw new ApplicationException("Failed to parse parameters");
            }

            if (this is IParametersLoadableState paramsLoadableVm)
            {
                Dispatcher.UIThread.Invoke(
                    () => paramsLoadableVm.LoadStateFromParameters(parameters)
                );
            }
            else
            {
                throw new InvalidOperationException(
                    "Load parameters target does not implement IParametersLoadableState"
                );
            }

            // Load image
            if (this is IImageGalleryComponent imageGalleryComponent)
            {
                Dispatcher.UIThread.Invoke(
                    () => imageGalleryComponent.LoadImagesToGallery(new ImageSource(filePath))
                );
            }
        }
        else
        {
            throw new ApplicationException("File does not contain any metadata");
        }
    }

    /// <inheritdoc />
    public void DragOver(object? sender, DragEventArgs e)
    {
        // 1. Context drop for LocalImageFile
        if (e.Data.GetDataFormats().Contains("Context"))
        {
            if (e.Data.Get("Context") is LocalImageFile imageFile)
            {
                e.Handled = true;
                return;
            }

            e.DragEffects = DragDropEffects.None;
        }
        // 2. OS Files
        if (e.Data.GetDataFormats().Contains(DataFormats.Files))
        {
            e.Handled = true;
            return;
        }

        // Other kinds - not supported
        e.DragEffects = DragDropEffects.None;
    }

    /// <inheritdoc />
    public void Drop(object? sender, DragEventArgs e)
    {
        // 1. Context drop for LocalImageFile
        if (e.Data.GetDataFormats().Contains("Context"))
        {
            if (e.Data.Get("Context") is LocalImageFile imageFile)
            {
                e.Handled = true;

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var metadata = imageFile.ReadMetadata();
                        if (metadata.SMProject is not null)
                        {
                            var project = JsonSerializer.Deserialize<InferenceProjectDocument>(
                                metadata.SMProject
                            );

                            // Check project type matches
                            if (
                                project?.ProjectType.ToViewModelType() == GetType()
                                && project.State is not null
                            )
                            {
                                LoadStateFromJsonObject(project.State);
                            }

                            // Load image
                            if (this is IImageGalleryComponent imageGalleryComponent)
                            {
                                imageGalleryComponent.LoadImagesToGallery(
                                    new ImageSource(imageFile.AbsolutePath)
                                );
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Failed to load image from context drop");
                        notificationService.ShowPersistent(
                            $"Could not parse image metadata",
                            $"{imageFile.FileName} - {ex.Message}",
                            NotificationType.Warning
                        );
                    }
                });

                return;
            }
        }
        // 2. OS Files
        if (e.Data.GetDataFormats().Contains(DataFormats.Files))
        {
            e.Handled = true;

            if (e.Data.Get(DataFormats.Files) is IEnumerable<IStorageItem> files)
            {
                if (files.Select(f => f.TryGetLocalPath()).FirstOrDefault() is { } path)
                {
                    var file = new FilePath(path);
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            LoadImageMetadata(file);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Failed to load image from OS file drop");
                            notificationService.ShowPersistent(
                                $"Could not parse image metadata",
                                $"{file.Name} - {ex.Message}",
                                NotificationType.Warning
                            );
                        }
                    });
                }
            }
        }
    }
}

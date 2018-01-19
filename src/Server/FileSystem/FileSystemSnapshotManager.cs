﻿// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VsChromium.Core.Collections;
using VsChromium.Core.Files;
using VsChromium.Core.Linq;
using VsChromium.Core.Logging;
using VsChromium.Core.Utility;
using VsChromium.Server.FileSystem.Builder;
using VsChromium.Server.FileSystemNames;
using VsChromium.Server.Operations;
using VsChromium.Server.Projects;
using VsChromium.Server.Threads;

namespace VsChromium.Server.FileSystem {
  [Export(typeof(IFileSystemSnapshotManager))]
  public class FileSystemSnapshotManager : IFileSystemSnapshotManager {
    private static readonly TaskId FlushPathsChangedQueueTaskId = new TaskId("FlushPathsChangedQueueTaskId");
    private static readonly TaskId FilesChangedTaskId = new TaskId("FilesChangedTaskId");
    private static readonly TaskId ProjectListChangedTaskId = new TaskId("ProjectListChangedTaskId");
    private static readonly TaskId FullRescanRequiredTaskId = new TaskId("FullRescanRequiredTaskId");

    /// <summary>
    /// Performance optimization flag: when building a new file system tree
    /// snapshot, this flag enables code to try to re-use filename instances
    /// from the previous snapshot. This is currently turned off, as profiling
    /// showed this slows down the algorithm by about 40% with not advantage
    /// other than decreasing GC activity. Note that this actually didn't
    /// decrease memory usage, as FileName instances are orphaned when a new
    /// snapshot is created (and the previous one is released).
    /// </summary>
    private static readonly bool ReuseFileNameInstances = false;

    private readonly IProjectDiscovery _projectDiscovery;
    private readonly IDirectoryChangeWatcher _directoryChangeWatcher;
    private readonly IFileSystemNameFactory _fileSystemNameFactory;
    private readonly IFileSystem _fileSystem;
    private readonly IFileSystemSnapshotBuilder _fileSystemSnapshotBuilder;
    private readonly IOperationProcessor _operationProcessor;
    private readonly ITaskQueue _longRunningFileSystemTaskQueue;
    private readonly ITaskQueue _flushPathChangesTaskQueue;
    private readonly ITaskQueue _stateChangeTaskQueue;

    private readonly SimpleConcurrentQueue<IList<PathChangeEntry>> _pathsChangedQueue =
      new SimpleConcurrentQueue<IList<PathChangeEntry>>();

    private readonly IFileRegistrationTracker _fileRegistrationTracker;

    /// <summary>
    /// Access to this field is serialized through tasks executed on the <see cref="_longRunningFileSystemTaskQueue"/>
    /// </summary>
    private IList<IProject> _registeredProjects = new List<IProject>();

    /// <summary>
    /// Access to this field is serialized through tasks executed on the  <see cref="_longRunningFileSystemTaskQueue"/>
    /// </summary>
    private FileSystemSnapshot _fileSystemSnapshot;

    private int _version;
    private bool _isWatchingDirectories;


    [ImportingConstructor]
    public FileSystemSnapshotManager(
      IFileSystemNameFactory fileSystemNameFactory,
      IFileSystem fileSystem,
      IFileRegistrationTracker fileRegistrationTracker,
      IFileSystemSnapshotBuilder fileSystemSnapshotBuilder,
      IOperationProcessor operationProcessor,
      IProjectDiscovery projectDiscovery,
      IDirectoryChangeWatcherFactory directoryChangeWatcherFactory,
      ITaskQueueFactory taskQueueFactory,
      ILongRunningFileSystemTaskQueue longRunningFileSystemTaskQueue) {
      _fileSystemNameFactory = fileSystemNameFactory;
      _fileSystem = fileSystem;
      _fileSystemSnapshotBuilder = fileSystemSnapshotBuilder;
      _operationProcessor = operationProcessor;
      _projectDiscovery = projectDiscovery;
      _longRunningFileSystemTaskQueue = longRunningFileSystemTaskQueue;
      _fileRegistrationTracker = fileRegistrationTracker;

      _flushPathChangesTaskQueue = taskQueueFactory.CreateQueue("FileSystemSnapshotManager Path Changes Task Queue");
      _stateChangeTaskQueue = taskQueueFactory.CreateQueue("FileSystemSnapshotManager State Change Task Queue");
      _fileRegistrationTracker.ProjectListChanged += FileRegistrationTrackerOnProjectListChanged;
      _fileRegistrationTracker.ProjectListRefreshed += FileRegistrationTrackerOnProjectListRefreshed;
      _fileSystemSnapshot = FileSystemSnapshot.Empty;
      _directoryChangeWatcher = directoryChangeWatcherFactory.CreateWatcher();
      _directoryChangeWatcher.PathsChanged += DirectoryChangeWatcherOnPathsChanged;
      _directoryChangeWatcher.Error += DirectoryChangeWatcherOnError;

      _isWatchingDirectories = true;
    }

    public FileSystemSnapshot CurrentSnapshot {
      get { return _fileSystemSnapshot; }
    }

    public void Pause() {
      _stateChangeTaskQueue.ExecuteAsync(token => {
        if (_isWatchingDirectories) {
          _directoryChangeWatcher.Stop();
          _longRunningFileSystemTaskQueue.CancelAll();
          _isWatchingDirectories = false;
          OnFileSystemWatchStopped(new FileSystemWatchStoppedEventArgs {
            IsError = false,
          });
        }
      });
    }

    public void Resume() {
      _stateChangeTaskQueue.ExecuteAsync(token => {
        if (!_isWatchingDirectories) {
          _directoryChangeWatcher.Start();
          _fileRegistrationTracker.RefreshAsync();
          _isWatchingDirectories = true;
        }
      });
    }

    public void Refresh() {
      _stateChangeTaskQueue.ExecuteAsync(token => {
        _longRunningFileSystemTaskQueue.CancelAll();
        _fileRegistrationTracker.RefreshAsync();
      });
    }

    public event EventHandler<OperationInfo> SnapshotScanStarted;
    public event EventHandler<SnapshotScanResult> SnapshotScanFinished;
    public event EventHandler<FilesChangedEventArgs> FilesChanged;
    public event EventHandler<FileSystemWatchStoppedEventArgs> FileSystemWatchStopped;

    protected virtual void OnSnapshotScanStarted(OperationInfo e) {
      SnapshotScanStarted?.Invoke(this, e);
    }

    protected virtual void OnSnapshotScanFinished(SnapshotScanResult e) {
      SnapshotScanFinished?.Invoke(this, e);
    }

    protected virtual void OnFilesChanged(FilesChangedEventArgs e) {
      FilesChanged?.Invoke(this, e);
    }

    protected virtual void OnFileSystemWatchStopped(FileSystemWatchStoppedEventArgs e) {
      FileSystemWatchStopped?.Invoke(this, e);
    }

    private void FileRegistrationTrackerOnProjectListChanged(object sender, ProjectsEventArgs e) {
      _stateChangeTaskQueue.ExecuteAsync(token => {
        Logger.LogInfo("List of projects has changed: Enqueuing a partial file system scan");

        // If we are queuing a task that requires rescanning the entire file system,
        // cancel existing tasks (should be only one really) to avoid wasting time
        _longRunningFileSystemTaskQueue.CancelAll();

        _longRunningFileSystemTaskQueue.Enqueue(ProjectListChangedTaskId, cancellationToken => {
          // Pass empty changes, as we don't know of any file system changes for
          // existing entries. For new entries, they don't exist in the snapshot,
          // so they will be read form disk
          var emptyChanges = new FullPathChanges(ArrayUtilities.EmptyList<PathChangeEntry>.Instance);
          RescanFileSystem(e.Projects, emptyChanges, cancellationToken);
        });
      });
    }

    private void FileRegistrationTrackerOnProjectListRefreshed(object sender, ProjectsEventArgs e) {
      _stateChangeTaskQueue.ExecuteAsync(token => {
        Logger.LogInfo("List of projects has been refreshed: Enqueuing a full file system scan");

        // If we are queuing a task that requires rescanning the entire file system,
        // cancel existing tasks (should be only one really) to avoid wasting time
        _longRunningFileSystemTaskQueue.CancelAll();

        _longRunningFileSystemTaskQueue.Enqueue(FullRescanRequiredTaskId, cancellationToken => {
          RescanFileSystem(e.Projects, null, cancellationToken);
        });
      });
    }

    private void DirectoryChangeWatcherOnPathsChanged(IList<PathChangeEntry> changes) {
      _stateChangeTaskQueue.ExecuteAsync(token => {
        if (_isWatchingDirectories) {
          Logger.LogInfo("File change events: enqueuing an incremental file system rescan");
          _pathsChangedQueue.Enqueue(changes);
          _flushPathChangesTaskQueue.Enqueue(FlushPathsChangedQueueTaskId, FlushPathsChangedQueueTask);
        }
      });
    }

    private void DirectoryChangeWatcherOnError(Exception exception) {
      _stateChangeTaskQueue.ExecuteAsync(token => {
        Logger.LogInfo("File change events error: entering pause mode");
        // Ingore all changes
        _pathsChangedQueue.DequeueAll();

        // If we are in a runnin state, pause due to an error
        _isWatchingDirectories = false;
        _longRunningFileSystemTaskQueue.CancelAll();
        OnFileSystemWatchStopped(new FileSystemWatchStoppedEventArgs { IsError = true });
      });
    }

    private void FlushPathsChangedQueueTask(CancellationToken cancellationToken) {
      cancellationToken.ThrowIfCancellationRequested();
      ProcessPendingPathChanges();
    }

    private void ProcessPendingPathChanges() {
      var changes = _pathsChangedQueue
        .DequeueAll()
        .SelectMany(x => x)
        .ToList();

      var validationResult = new FileSystemChangesValidator(_fileSystemNameFactory, _fileSystem, _projectDiscovery)
        .ProcessPathsChangedEvent(changes);

      switch (validationResult.Kind) {
        case FileSystemValidationResultKind.NoChanges:
          return;

        case FileSystemValidationResultKind.FileModificationsOnly:
          OnFilesChanged(new FilesChangedEventArgs {
            ChangedFiles = validationResult.ModifiedFiles.ToReadOnlyCollection()
          });
          return;

        case FileSystemValidationResultKind.VariousFileChanges:
          _longRunningFileSystemTaskQueue.Enqueue(FilesChangedTaskId, cancellationToken => {
            RescanFileSystem(_registeredProjects, validationResult.FileChanges, cancellationToken);
          });
          return;

        case FileSystemValidationResultKind.UnknownChanges:
          _fileRegistrationTracker.RefreshAsync();
          return;

        default:
          Debug.Assert(false, "What kind of validation result is this?");
          return;
      }
    }

    private void RescanFileSystem(IList<IProject> projects, FullPathChanges pathChanges /* may be null */,
      CancellationToken cancellationToken) {
      _registeredProjects = projects;

      _operationProcessor.Execute(new OperationHandlers {

        OnBeforeExecute = info =>
          OnSnapshotScanStarted(info),

        OnError = (info, error) => {
          if (!error.IsCanceled()) {
            Logger.LogError(error, "File system rescan error");
          }
          OnSnapshotScanFinished(new SnapshotScanResult {
            OperationInfo = info,
            Error = error
          });
        },

        Execute = info => {
          // Compute and assign new snapshot
          var oldSnapshot = _fileSystemSnapshot;
          var newSnapshot = BuildNewFileSystemSnapshot(projects, oldSnapshot, pathChanges, cancellationToken);
          // Update of new tree (assert calls are serialized).
          Debug.Assert(ReferenceEquals(oldSnapshot, _fileSystemSnapshot));
          _fileSystemSnapshot = newSnapshot;

          if (Logger.Info) {
            Logger.LogInfo("+++++++++++ Collected {0:n0} files in {1:n0} directories",
              newSnapshot.ProjectRoots.Aggregate(0, (acc, x) => acc + CountFileEntries(x.Directory)),
              newSnapshot.ProjectRoots.Aggregate(0, (acc, x) => acc + CountDirectoryEntries(x.Directory)));
          }

          // Post event
          OnSnapshotScanFinished(new SnapshotScanResult {
            OperationInfo = info,
            PreviousSnapshot = oldSnapshot,
            FullPathChanges = pathChanges,
            NewSnapshot = newSnapshot
          });
        }
      });
    }

    private FileSystemSnapshot BuildNewFileSystemSnapshot(IList<IProject> projects,
      FileSystemSnapshot oldSnapshot, FullPathChanges pathChanges, CancellationToken cancellationToken) {

      using (new TimeElapsedLogger("Computing snapshot delta from list of file changes")) {
        // file name factory
        var fileNameFactory = _fileSystemNameFactory;
        if (ReuseFileNameInstances) {
          if (oldSnapshot.ProjectRoots.Count > 0) {
            fileNameFactory = new FileSystemTreeSnapshotNameFactory(oldSnapshot, fileNameFactory);
          }
        }

        // Compute new snapshot
        var newSnapshot = _fileSystemSnapshotBuilder.Compute(
          fileNameFactory,
          oldSnapshot,
          pathChanges,
          projects,
          Interlocked.Increment(ref _version),
          cancellationToken);

        // Monitor all project roots for file changes.
        var newRoots = newSnapshot.ProjectRoots
          .Select(entry => entry.Directory.DirectoryName.FullPath);
        _directoryChangeWatcher.WatchDirectories(newRoots);

        return newSnapshot;
      }
    }

    private int CountFileEntries(DirectorySnapshot entry) {
      return
        entry.ChildFiles.Count +
        entry.ChildDirectories.Aggregate(0, (acc, x) => acc + CountFileEntries(x));
    }

    private int CountDirectoryEntries(DirectorySnapshot entry) {
      return
        1 +
        entry.ChildDirectories.Aggregate(0, (acc, x) => acc + CountDirectoryEntries(x));
    }
  }
}
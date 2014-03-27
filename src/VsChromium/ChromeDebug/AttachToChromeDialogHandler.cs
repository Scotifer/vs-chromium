// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.DefaultPort;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel.Composition;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VsChromium.Core.Processes;
using VsChromium.Package;
using VsChromium.Package.CommandHandler;

namespace VsChromium.ChromeDebug {
  [Export(typeof(IPackageCommandHandler))]
  public class AttachToChromeDialogHandler : IPackageCommandHandler {
    private readonly IVisualStudioPackageProvider _visualStudioPackageProvider;

    [ImportingConstructor]
    public AttachToChromeDialogHandler(IVisualStudioPackageProvider visualStudioPackageProvider) {
      _visualStudioPackageProvider = visualStudioPackageProvider;
    }

    public CommandID CommandId { get { return new CommandID(GuidList.GuidChromeDebugCmdSet, (int)PkgCmdIDList.CmdidAttachToChromeDialog); } }

    public void Execute(object sender, EventArgs e) {
      // Show a Message Box to prove we were here
      var dte = (EnvDTE.DTE)_visualStudioPackageProvider.Package.DTE; //GetService(typeof(EnvDTE.DTE));

      var uiShell = _visualStudioPackageProvider.Package.VsUIShell;
      var parentHwnd = IntPtr.Zero;
      uiShell.GetDialogOwnerHwnd(out parentHwnd);

      var parentShim = new NativeWindow();
      parentShim.AssignHandle(parentHwnd);
      var dialog = new AttachDialog();
      var result = dialog.ShowDialog(parentShim);
      if (result == DialogResult.OK) {
        HashSet<Process> processes = new HashSet<Process>();
        foreach (int pid in dialog.SelectedItems) {
          Process p = Process.GetProcessById(pid);
          if (!p.IsBeingDebugged())
            processes.Add(p);

          if (dialog.AutoAttachToCurrentChildren) {
            foreach (Process child in p.GetChildren()) {
              if (!child.IsBeingDebugged())
                processes.Add(child);
            }
          }
        }
        List<Process> processList = new List<Process>(processes);
        DebugAttach.AttachToProcess(processList.ToArray(), dialog.AutoAttachToFutureChildren);
      }
    }
  }
}

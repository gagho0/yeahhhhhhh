﻿using System;
using System.Windows.Forms;

namespace OpenMLTD.ManifestTools.UI {
    public class ExplorerTreeView : TreeView {

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);

            if (!DesignMode && OSUtil.IsWindowsVistaOrLater()) {
                NativeMethods.SetWindowTheme(Handle, "explorer", null);
            }
        }

    }
}

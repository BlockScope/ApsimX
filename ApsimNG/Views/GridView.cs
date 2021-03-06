﻿// -----------------------------------------------------------------------
// <copyright file="GridView.cs" company="APSIM Initiative">
//     Copyright (c) APSIM Initiative
// </copyright>
// -----------------------------------------------------------------------
namespace UserInterface.Views
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using Classes;
    //using DataGridViewAutoFilter;
    using EventArguments;
    using Glade;
    using Gtk;
    using Interfaces;
    using Models.Core;

    /// <summary>
    /// We want to have a "button" we can press within a grid cell. We could use a Gtk CellRendererPixbuf for this, 
    /// but that doesn't provide an easy way to detect a button press, so instead we can use a "toggle", but 
    /// override the Render function to simply display our Pixbuf
    /// </summary>
    public class CellRendererActiveButton : CellRendererToggle
    {
        protected override void Render(Gdk.Drawable window, Widget widget, Gdk.Rectangle background_area, Gdk.Rectangle cell_area, Gdk.Rectangle expose_area, CellRendererState flags)
        {
            Gdk.GC gc = new Gdk.GC(window);
            window.DrawPixbuf(gc, pixbuf, 0, 0, cell_area.X, cell_area.Y, pixbuf.Width, pixbuf.Height, Gdk.RgbDither.Normal, 0, 0);
        }

        public Gdk.Pixbuf pixbuf { get; set;  }
    }

    /// <summary>
    /// A grid control that implements the grid view interface.
    /// </summary>
    public class GridView : ViewBase, IGridView
    {
        /// <summary>
        /// This event is invoked when the values of 1 or more cells have changed.
        /// </summary>
        public event EventHandler<GridCellsChangedArgs> CellsChanged;

        /// <summary>
        /// Invoked when a grid cell header is clicked.
        /// </summary>
        public event EventHandler<GridHeaderClickedArgs> ColumnHeaderClicked;

        /// <summary>Occurs when user clicks a button on the cell.</summary>
        public event EventHandler<GridCellsChangedArgs> ButtonClick;

        /// <summary>
        /// Is the user currently editing a cell?
        /// </summary>
        private bool userEditingCell = false;

        /// <summary>
        /// The value before the user starts editing a cell.
        /// </summary>
        private object valueBeforeEdit;

        /// <summary>
        /// The data table that is being shown on the grid.
        /// </summary>
        private DataTable table;

        /// <summary>
        /// A value indicating whether auto filter (whatever that is) is turned on.
        /// We don't currently use this in the Gtk GUI.
        /// </summary>
        private bool isAutoFilterOn = false;

        /// <summary>
        /// The default numeric format
        /// </summary>
        private string defaultNumericFormat = "F2";

        /// <summary>
        /// Flag to keep track of whether a cursor move was initiated internally
        /// </summary>
        private bool selfCursorMove = false;

        [Widget]
        private ScrolledWindow scrolledwindow1 = null;
        // [Widget]
        // private ScrolledWindow scrolledwindow2 = null;

        [Widget]
        public TreeView gridview = null;
        [Widget]
        public TreeView fixedcolview = null;

        [Widget]
        private HBox hbox1 = null;
        [Widget]
        private Gtk.Image image1 = null;

        private Gdk.Pixbuf imagePixbuf;

        private ListStore gridmodel = new ListStore(typeof(string));
        private Dictionary<CellRenderer, int> colLookup = new Dictionary<CellRenderer, int>();
        internal Dictionary<Tuple<int, int>, ListStore> comboLookup = new Dictionary<Tuple<int, int>, ListStore>();
        internal List<Tuple<int, int>> buttonList = new List<Tuple<int, int>>();
        private Menu Popup = new Menu();
        private AccelGroup accel = new AccelGroup();
        private GridCell popupCell = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="GridView" /> class.
        /// </summary>
        public GridView(ViewBase owner) : base(owner)
        {
            Glade.XML gxml = new Glade.XML("ApsimNG.Resources.Glade.GridView.glade", "hbox1");
            gxml.Autoconnect(this);
            _mainWidget = hbox1;
            gridview.Model = gridmodel;
            gridview.Selection.Mode = SelectionMode.Multiple;
            fixedcolview.Model = gridmodel;
            fixedcolview.Selection.Mode = SelectionMode.Multiple;
            Popup.AttachToWidget(gridview, null);
            AddContextActionWithAccel("Copy", OnCopyToClipboard, "Ctrl+C");
            AddContextActionWithAccel("Paste", OnPasteFromClipboard, "Ctrl+V");
            AddContextActionWithAccel("Delete", OnDeleteClick, "Delete");
            gridview.ButtonPressEvent += OnButtonDown;
            fixedcolview.ButtonPressEvent += OnButtonDown;
            gridview.FocusInEvent += FocusInEvent;
            gridview.FocusOutEvent += FocusOutEvent;
            gridview.KeyPressEvent += Gridview_KeyPressEvent;
            gridview.EnableSearch = false;
            fixedcolview.FocusInEvent += FocusInEvent;
            fixedcolview.FocusOutEvent += FocusOutEvent;
            fixedcolview.EnableSearch = false;
            image1.Pixbuf = null;
            image1.Visible = false;
            _mainWidget.Destroyed += _mainWidget_Destroyed;
        }

        /// <summary>
        /// Does cleanup when the main widget is destroyed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void _mainWidget_Destroyed(object sender, EventArgs e)
        {
            if (numberLockedCols > 0)
            {
                gridview.Vadjustment.ValueChanged -= Gridview_Vadjustment_Changed;
                gridview.Selection.Changed -= Gridview_CursorChanged;
                fixedcolview.Vadjustment.ValueChanged -= Fixedcolview_Vadjustment_Changed1;
                fixedcolview.Selection.Changed -= Fixedcolview_CursorChanged;
            }
            gridview.ButtonPressEvent -= OnButtonDown;
            fixedcolview.ButtonPressEvent -= OnButtonDown;
            gridview.FocusInEvent -= FocusInEvent;
            gridview.FocusOutEvent -= FocusOutEvent;
            gridview.KeyPressEvent -= Gridview_KeyPressEvent;
            fixedcolview.FocusInEvent -= FocusInEvent;
            fixedcolview.FocusOutEvent -= FocusOutEvent;
            // It's good practice to disconnect the event handlers, as it makes memory leaks
            // less likely. However, we may not "own" the event handlers, so how do we 
            // know what to disconnect?
            // We can do this via reflection. Here's how it currently can be done in Gtk#.
            // Windows.Forms would do it differently.
            // This may break if Gtk# changes the way they implement event handlers.
            foreach (Widget w in Popup)
            {
                if (w is ImageMenuItem)
                {
                    PropertyInfo pi = w.GetType().GetProperty("AfterSignals", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi != null)
                    {
                        System.Collections.Hashtable handlers = (System.Collections.Hashtable)pi.GetValue(w);
                        if (handlers != null && handlers.ContainsKey("activate"))
                        {
                            EventHandler handler = (EventHandler)handlers["activate"];
                            (w as ImageMenuItem).Activated -= handler;
                        }
                    }
                }
            }
            ClearGridColumns();
        }

        /// <summary>
        /// Removes all grid columns, and cleans up any associated event handlers
        /// </summary>
        private void ClearGridColumns()
        {
            while (gridview.Columns.Length > 0)
            {
                TreeViewColumn col = gridview.GetColumn(0);
                foreach (CellRenderer render in col.CellRenderers)
                {
                    if (render is CellRendererText)
                    {
                        CellRendererText textRender = render as CellRendererText;
                        textRender.EditingStarted -= OnCellBeginEdit;
                        textRender.EditingCanceled -= TextRender_EditingCanceled;
                        textRender.Edited -= OnCellValueChanged;
                        col.SetCellDataFunc(textRender, (CellLayoutDataFunc)null);
                    }
                    else if (render is CellRendererActiveButton)
                    {
                        (render as CellRendererActiveButton).Toggled -= PixbufRender_Toggled;
                    }
                    else if (render is CellRendererToggle)
                    {
                        (render as CellRendererToggle).Toggled -= ToggleRender_Toggled;
                    }
                    else if (render is CellRendererCombo)
                    {
                        (render as CellRendererCombo).Edited -= ComboRender_Edited;
                    }
                }
                gridview.RemoveColumn(gridview.GetColumn(0));
            }
            while (fixedcolview.Columns.Length > 0)
            {
                TreeViewColumn col = fixedcolview.GetColumn(0);
                foreach (CellRenderer render in col.CellRenderers)
                    if (render is CellRendererText)
                    {
                        CellRendererText textRender = render as CellRendererText;
                        textRender.EditingStarted -= OnCellBeginEdit;
                        textRender.EditingCanceled -= TextRender_EditingCanceled;
                        textRender.Edited -= OnCellValueChanged;
                        col.SetCellDataFunc(textRender, (CellLayoutDataFunc)null);
                    }
                fixedcolview.RemoveColumn(fixedcolview.GetColumn(0));
            }
        }

        /// <summary>
        /// Intercepts key press events
        /// The main reason for doing this is to allow the user to move to the "next" cell
        /// when editing, and either the tab or return key is pressed.
        /// </summary>
        /// <param name="o"></param>
        /// <param name="args"></param>
        [GLib.ConnectBefore]
        private void Gridview_KeyPressEvent(object o, KeyPressEventArgs args)
        {
            string keyName = Gdk.Keyval.Name(args.Event.KeyValue);
            IGridCell cell = GetCurrentCell;
            if (cell == null)
                return;
            int iRow = cell.RowIndex;
            int iCol = cell.ColumnIndex;

            if (keyName == "F2" && !userEditingCell) // Allow f2 to initiate cell editing
            {
                if (!this.GetColumn(iCol).ReadOnly)
                {
                    gridview.SetCursor(new TreePath(new int[1] { iRow }), gridview.GetColumn(iCol), true);
                }
            }
            if (keyName == "ISO_Left_Tab")
                keyName = "Tab";
            if ((keyName == "Return" || keyName == "Tab") && userEditingCell)
            {
                bool shifted = (args.Event.State & Gdk.ModifierType.ShiftMask) != 0;
                int nextRow = iRow;
                int nCols = DataSource != null ? this.DataSource.Columns.Count : 0;
                int nextCol = iCol;
                if (shifted) // Move backwards
                {
                    do
                    {
                        if (keyName == "Tab") // Move horizontally
                        {
                            if (--nextCol < 0)
                            {
                                if (--nextRow < 0)
                                    nextRow = RowCount - 1;
                                nextCol = nCols - 1;
                            }
                        }
                        else if (keyName == "Return") // Move vertically
                        {
                            if (--nextRow < 0)
                            {
                                if (--nextCol < 0)
                                    nextCol = nCols - 1;
                                nextRow = RowCount - 1;
                            }
                        }
                    }
                    while (this.GetColumn(nextCol).ReadOnly || !(new GridCell(this, nextCol, nextRow).EditorType == EditorTypeEnum.TextBox));
                }
                else
                {
                    do
                    {
                        if (keyName == "Tab") // Move horizontally
                        {
                            if (++nextCol >= nCols)
                            {
                                if (++nextRow >= RowCount)
                                    nextRow = 0;
                                nextCol = 0;
                            }
                        }
                        else if (keyName == "Return") // Move vertically
                        {
                            if (++nextRow >= RowCount)
                            {
                                if (++nextCol >= nCols)
                                    nextCol = 0;
                                nextRow = 0;
                            }
                        }
                    }
                    while (this.GetColumn(nextCol).ReadOnly || !(new GridCell(this, nextCol, nextRow).EditorType == EditorTypeEnum.TextBox));
                }
                EndEdit();
                while (GLib.MainContext.Iteration())
                    ;
                if (nextRow != iRow || nextCol != iCol)
                    gridview.SetCursor(new TreePath(new int[1] { nextRow }), gridview.GetColumn(nextCol), true);
                args.RetVal = true;
            }
        }

        /// <summary>
        /// Ensure that we save any changes made when the editing control loses focus
        /// Note that we need to handle loss of the editing control's focus, not that
        /// of the gridview overall
        /// </summary>
        /// <param name="o"></param>
        /// <param name="args"></param>
        [GLib.ConnectBefore]
        private void GridViewCell_FocusOutEvent(object o, FocusOutEventArgs args)
        {
            EndEdit();
        }

        /// <summary>
        /// Repsonds to selection changes in the "fixed" columns area by
        /// selecting corresponding rows in the main grid
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Fixedcolview_CursorChanged(object sender, EventArgs e)
        {
            if (!selfCursorMove)
            {
                selfCursorMove = true;
                TreeSelection fixedSel = fixedcolview.Selection;
                TreePath[] selPaths = fixedSel.GetSelectedRows();

                TreeSelection gridSel = gridview.Selection;
                gridSel.UnselectAll();
                foreach (TreePath path in selPaths)
                    gridSel.SelectPath(path);
                selfCursorMove = false;
            }
        }

        /// <summary>
        /// Repsonds to selection changes in the main grid by
        /// selecting corresponding rows in the "fixed columns" grid
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Gridview_CursorChanged(object sender, EventArgs e)
        {
            if (fixedcolview.Visible && !selfCursorMove)
            {
                selfCursorMove = true;
                TreeSelection gridSel = gridview.Selection;
                TreePath[] selPaths = gridSel.GetSelectedRows();

                TreeSelection fixedSel = fixedcolview.Selection;
                fixedSel.UnselectAll();
                foreach (TreePath path in selPaths)
                    fixedSel.SelectPath(path);
                selfCursorMove = false;
            }
        }

        private int numberLockedCols = 0;

        /// <summary>
        /// Gets or sets the data to use to populate the grid.
        /// </summary>
        public System.Data.DataTable DataSource
        {
            get
            {
                return this.table;
            }
            
            set
            {
                this.table = value;
                LockLeftMostColumns(0);
                this.PopulateGrid();
            }
        }

        /// <summary>
        /// Populate the grid from the DataSource.
        /// Note that we don't statically set the contents of the grid cells, but rather do this 
        /// dynamically in OnSetCellData. However, we do set up appropriate attributes for 
        /// cell columns, and a set of cell renderers.
        /// </summary>
        private void PopulateGrid()
        {
            // WaitCursor = true;
            // Set the cursor directly rather than via the WaitCursor property, as the property setter
            // runs a message loop. This is normally desirable, but in this case, we have lots
            // of events associated with the grid data, and it's best to let them be handled in the 
            // main message loop. 

            if (mainWindow != null)
               mainWindow.Cursor = new Gdk.Cursor(Gdk.CursorType.Watch);
            ClearGridColumns();
            fixedcolview.Visible = false;
            colLookup.Clear();
            // Begin by creating a new ListStore with the appropriate number of
            // columns. Use the string column type for everything.
            int nCols = DataSource != null ? this.DataSource.Columns.Count : 0;
            Type[] colTypes = new Type[nCols];
            for (int i = 0; i < nCols; i++)
                colTypes[i] = typeof(string);
            gridmodel = new ListStore(colTypes);
            gridview.ModifyBase(StateType.Active, fixedcolview.Style.Base(StateType.Selected));
            gridview.ModifyText(StateType.Active, fixedcolview.Style.Text(StateType.Selected));
            fixedcolview.ModifyBase(StateType.Active, gridview.Style.Base(StateType.Selected));
            fixedcolview.ModifyText(StateType.Active, gridview.Style.Text(StateType.Selected));

            image1.Visible = false;
            // Now set up the grid columns
            for (int i = 0; i < nCols; i++)
            {
                /// Design plan: include renderers for text, toggles and combos, but hide all but one of them
                CellRendererText textRender = new Gtk.CellRendererText();
                CellRendererToggle toggleRender = new Gtk.CellRendererToggle();
                toggleRender.Visible = false;
                toggleRender.Toggled += ToggleRender_Toggled;
                toggleRender.Xalign = ((i == 1) && isPropertyMode) ? 0.0f : 0.5f; // Left of center align, as appropriate
                CellRendererCombo comboRender = new Gtk.CellRendererCombo();
                comboRender.Edited += ComboRender_Edited;
                comboRender.Xalign = ((i == 1) && isPropertyMode) ? 0.0f : 1.0f; // Left or right align, as appropriate
                comboRender.Visible = false;
                CellRendererActiveButton pixbufRender = new CellRendererActiveButton();
                pixbufRender.pixbuf = new Gdk.Pixbuf(null, "ApsimNG.Resources.MenuImages.Save.png");
                pixbufRender.Toggled += PixbufRender_Toggled;

                colLookup.Add(textRender, i);

                textRender.FixedHeightFromFont = 1; // 1 line high
                textRender.Editable = !isReadOnly;
                textRender.EditingStarted += OnCellBeginEdit;
                textRender.EditingCanceled += TextRender_EditingCanceled;
                textRender.Edited += OnCellValueChanged;
                textRender.Xalign = ((i == 0) || (i == 1) && isPropertyMode) ? 0.0f : 1.0f; // For right alignment of text cell contents; left align the first column

                TreeViewColumn column = new TreeViewColumn();
                column.Title = this.DataSource.Columns[i].ColumnName;
                column.PackStart(textRender, true);     // 0
                column.PackStart(toggleRender, true);   // 1
                column.PackStart(comboRender, true);    // 2
                column.PackStart(pixbufRender, false);  // 3

                column.Sizing = TreeViewColumnSizing.Autosize;
                //column.FixedWidth = 100;
                column.Resizable = true;
                column.SetCellDataFunc(textRender, OnSetCellData);
                if (i == 1 && isPropertyMode)
                    column.Alignment = 0.0f;
                else
                    column.Alignment = 0.5f; // For centered alignment of the column header
                gridview.AppendColumn(column);

                // Gtk Treeview doesn't support "frozen" columns, so we fake it by creating a second, identical, TreeView to display
                // the columns we want frozen
                // For now, these frozen columns will be treated as read-only text
                TreeViewColumn fixedColumn = new TreeViewColumn(this.DataSource.Columns[i].ColumnName, textRender, "text", i);
                fixedColumn.Sizing = TreeViewColumnSizing.Autosize;
                fixedColumn.Resizable = true;
                fixedColumn.SetCellDataFunc(textRender, OnSetCellData);
                fixedColumn.Alignment = 0.5f; // For centered alignment of the column header
                fixedColumn.Visible = false;
                fixedcolview.AppendColumn(fixedColumn);
            }

            if (!isPropertyMode)
            {
                // Add an empty column at the end; auto-sizing will give this any "leftover" space
                TreeViewColumn fillColumn = new TreeViewColumn();
                gridview.AppendColumn(fillColumn);
                fillColumn.Sizing = TreeViewColumnSizing.Autosize;
            }

            int nRows = DataSource != null ? this.DataSource.Rows.Count : 0;

            gridview.Model = null;
            fixedcolview.Model = null;
            for (int row = 0; row < nRows; row++)
            {
                // We could store data into the grid model, but we don't.
                // Instead, we retrieve the data from our datastore when the OnSetCellData function is called
                gridmodel.Append();
            }
            gridview.Model = gridmodel;

            gridview.Show();

            // Now let's apply center-justification to all the column headers, just for the heck of it
            // It seems that on Windows, it's best to do this after gridview has been shown
            // Note that this affects the justification of wrapped lines, not justification of the
            // header as a whole, which is handled with column.Alignment
            for (int i = 0; i < nCols; i++)
            {
                Label label = GetColumnHeaderLabel(i);
                if (label != null)
                {
                    label.Wrap = true;
                    label.Justify = Justification.Center;
                    if (i == 1 && isPropertyMode)  // Add a tiny bit of extra space when left-aligned
                        (label.Parent as Alignment).LeftPadding = 2;
                    label.Style.FontDescription.Weight = Pango.Weight.Bold;
                }

                label = GetColumnHeaderLabel(i, fixedcolview);
                if (label != null)
                {
                    label.Wrap = true;
                    label.Justify = Justification.Center;
                    label.Style.FontDescription.Weight = Pango.Weight.Bold;
                }
            }

            if (mainWindow != null)
                mainWindow.Cursor = null;
        }

        /// <summary>
        /// Clean up "stuff" when the editing control is closed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TextRender_EditingCanceled(object sender, EventArgs e)
        {
            this.userEditingCell = false;
            (this.editControl as Widget).KeyPressEvent -= Gridview_KeyPressEvent;
            (this.editControl as Widget).FocusOutEvent -= GridViewCell_FocusOutEvent;
            this.editControl = null;
        }

        /// <summary>
        /// Handle vertical scrolling changes to keep the gridview and fixedcolview at the same scrolled position
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Fixedcolview_Vadjustment_Changed1(object sender, EventArgs e)
        {
            gridview.Vadjustment.Value = fixedcolview.Vadjustment.Value;
        }

        /// <summary>
        /// Handle vertical scrolling changes to keep the gridview and fixedcolview at the same scrolled position
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Gridview_Vadjustment_Changed(object sender, EventArgs e)
        {
            fixedcolview.Vadjustment.Value = gridview.Vadjustment.Value;
        }

        /// <summary>
        /// Sets the contents of a cell being display on a grid
        /// </summary>
        /// <param name="col"></param>
        /// <param name="cell"></param>
        /// <param name="model"></param>
        /// <param name="iter"></param>
        public void OnSetCellData(TreeViewColumn col, CellRenderer cell, TreeModel model, TreeIter iter)
        {
            TreePath path = model.GetPath(iter);
            TreeView view = col.TreeView as TreeView;
            int rowNo = path.Indices[0];
            int colNo;
            string text = String.Empty;
            if (colLookup.TryGetValue(cell, out colNo) && rowNo < this.DataSource.Rows.Count && colNo < this.DataSource.Columns.Count)
            {
                if (view == gridview)
                {
                    col.CellRenderers[1].Visible = false;
                    col.CellRenderers[2].Visible = false;
                    col.CellRenderers[3].Visible = false;
                }
                object dataVal = this.DataSource.Rows[rowNo][colNo];
                Type dataType = dataVal.GetType();
                if (dataType == typeof(DBNull))
                    text = String.Empty;
                else if (NumericFormat != null && ((dataType == typeof(float) && !float.IsNaN((float)dataVal)) ||
                    (dataType == typeof(double) && !Double.IsNaN((double)dataVal))))
                    text = String.Format("{0:" + NumericFormat + "}", dataVal);
                else if (dataType == typeof(DateTime))
                    text = String.Format("{0:d}", dataVal);
                else if (view == gridview)  // Currently not handling booleans and lists in the "fixed" column grid
                {
                    if (dataType == typeof(Boolean))
                    {
                        CellRendererToggle toggleRend = col.CellRenderers[1] as CellRendererToggle;
                        if (toggleRend != null)
                        {
                            toggleRend.Active = (Boolean)dataVal;
                            toggleRend.Activatable = true;
                            cell.Visible = false;
                            col.CellRenderers[2].Visible = false;
                            toggleRend.Visible = true;
                            return;
                        }
                    }
                    else
                    {   // This assumes that combobox grid cells are based on the "string" type
                        Tuple<int, int> location = new Tuple<int, int>(rowNo, colNo);
                        ListStore store;
                        if (comboLookup.TryGetValue(location, out store))
                        {
                            CellRendererCombo comboRend = col.CellRenderers[2] as CellRendererCombo;
                            if (comboRend != null)
                            {
                                comboRend.Model = store;
                                comboRend.TextColumn = 0;
                                comboRend.Editable = true;
                                comboRend.HasEntry = false;
                                cell.Visible = false;
                                col.CellRenderers[1].Visible = false;
                                comboRend.Visible = true;
                                comboRend.Text = AsString(dataVal);
                                return;
                            }
                        }
                        if (buttonList.Contains(location))
                        {
                            CellRendererActiveButton buttonRend = col.CellRenderers[3] as CellRendererActiveButton;
                            if (buttonRend != null)
                            {
                                buttonRend.Visible = true;
                            }
                        }
                        text = AsString(dataVal);
                    }
                }
                else
                {
                    text = AsString(dataVal);
                }
            }
            cell.Visible = true;
            (cell as CellRendererText).Text = text;
        }

        /// <summary>
        /// The name of the associated model.
        /// </summary>
        public string ModelName
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of rows in grid.
        /// </summary>
        public int RowCount
        {
            get
            {
                return gridmodel.IterNChildren();
            }
            
            set
            {
                // The main use of this will be to allow "empty" rows at the bottom of the grid to allow for
                // additional data to be entered (primarily soil profile stuff). 
                if (value > RowCount) // Add new rows
                {
                    for (int i = RowCount; i < value; i++)
                        gridmodel.Append(); // Will this suffice?
                }
                else if (value < RowCount) // Remove existing rows. But let's check first to be sure they're empty
                {
                    /// TBI
                }
                /// TBI this.Grid.RowCount = value;
            }
        }

        /// <summary>
        /// Gets or sets the numeric grid format e.g. N3
        /// </summary>
        public string NumericFormat
        {
            get
            {
                return this.defaultNumericFormat;
            }

            set
            {
                this.defaultNumericFormat = value;
            }
        }

        private bool isPropertyMode = false;

        /// <summary>
        /// Gets or sets a value indicating whether "property" mode is enabled
        /// </summary>
        public bool PropertyMode
        {
            get
            {
                return isPropertyMode;
            }
            set
            {
                if (value != isPropertyMode)
                {
                    this.PopulateGrid();
                }
                isPropertyMode = value;
            }
        }

        /// <summary>
        /// Stores whether our grid is readonly. Internal value.
        /// </summary>
        private bool isReadOnly = false;

        /// <summary>
        /// Gets or sets a value indicating whether the grid is read only
        /// </summary>
        public bool ReadOnly 
        { 
            get 
            {
                return isReadOnly; 
            } 
            
            set 
            {
                if (value != isReadOnly)
                {
                    foreach (TreeViewColumn col in gridview.Columns)
                        foreach (CellRenderer render in col.CellRenderers)
                            if (render is CellRendererText)
                                (render as CellRendererText).Editable = !value;
                }
                isReadOnly = value;
            } 
        }

        /// <summary>
        /// Gets or sets a value indicating whether the grid has an auto filter
        /// </summary>
        public bool AutoFilterOn
        {
            get
            {
                return this.isAutoFilterOn;
            }
            
            set 
            {

                // MONO doesn't seem to like the auto filter option.
                if (Environment.OSVersion.Platform == PlatformID.Win32NT ||
                    Environment.OSVersion.Platform == PlatformID.Win32Windows)
                {
                    this.isAutoFilterOn = value;
                    this.PopulateGrid();
                }    
            }
        }

        /// <summary>
        /// Gets or sets the currently selected cell. Null if none selected.
        /// </summary>
        public IGridCell GetCurrentCell
        {
            get
            {
                TreePath path;
                TreeViewColumn col;
                gridview.GetCursor(out path, out col);
                if (path != null && col != null && col.Cells.Length > 0)
                {
                    int colNo, rowNo;
                    rowNo = path.Indices[0];
                    if (colLookup.TryGetValue(col.Cells[0], out colNo))
                      return this.GetCell(colNo, rowNo);
                }
                return null;
            }

            set
            {
                if (value != null)
                {
                    TreePath row = new TreePath(new int[1] { value.RowIndex });
                    gridview.SetCursor(row, gridview.GetColumn(value.ColumnIndex), false);
                }
            }
        }

        /// <summary>
        /// Return a particular cell of the grid.
        /// </summary>
        /// <param name="columnIndex">The column index</param>
        /// <param name="rowIndex">The row index</param>
        /// <returns>The cell</returns>
        public IGridCell GetCell(int columnIndex, int rowIndex)
        {
            return new GridCell(this, columnIndex, rowIndex);
        }

        /// <summary>
        /// Returns the string representation of an object. For most objects,
        /// this will be the same as "ToString()", but for Crops, it will give
        /// the crop name
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        private string AsString(object obj)
        {
            string result;
            if (obj is ICrop)
                result = (obj as IModel).Name;
            else
                result = obj.ToString();
            return result;
        }

        /// <summary>
        /// Return a particular column of the grid.
        /// </summary>
        /// <param name="columnIndex">The column index</param>
        /// <returns>The column</returns>
        public IGridColumn GetColumn(int columnIndex)
        {
            return new GridColumn(this, columnIndex);
        }

        /// <summary>
        /// Add an action (on context menu) on the series grid.
        /// </summary>
        /// <param name="menuItemText">The text of the menu item</param>
        /// <param name="onClick">The event handler to call when menu is selected</param>
        public void AddContextSeparator()
        {
            Popup.Append(new SeparatorMenuItem());
        }

        /// <summary>
        /// Add an action (on context menu) on the series grid.
        /// </summary>
        /// <param name="menuItemText">The text of the menu item</param>
        /// <param name="onClick">The event handler to call when menu is selected</param>
        public void AddContextAction(string menuItemText, System.EventHandler onClick)
        {
            ImageMenuItem item = new ImageMenuItem(menuItemText);
            item.Activated += onClick;
            Popup.Append(item);
            Popup.ShowAll();
        }

        /// <summary>
        /// Add an action (on context menu) on the series grid.
        /// </summary>
        /// <param name="menuItemText">The text of the menu item</param>
        /// <param name="onClick">The event handler to call when menu is selected</param>
        public void AddContextActionWithAccel(string menuItemText, System.EventHandler onClick, string shortcut)
        {
            ImageMenuItem item = new ImageMenuItem(menuItemText);
            if (!String.IsNullOrEmpty(shortcut))
            {
                string keyName = String.Empty;
                Gdk.ModifierType modifier = Gdk.ModifierType.None;
                string[] keyNames = shortcut.Split(new Char[] { '+' });
                foreach (string name in keyNames)
                {
                    if (name == "Ctrl")
                        modifier |= Gdk.ModifierType.ControlMask;
                    else if (name == "Shift")
                        modifier |= Gdk.ModifierType.ShiftMask;
                    else if (name == "Alt")
                        modifier |= Gdk.ModifierType.Mod1Mask;
                    else if (name == "Del")
                        keyName = "Delete";
                    else
                        keyName = name;
                }
                try
                {
                    Gdk.Key accelKey = (Gdk.Key)Enum.Parse(typeof(Gdk.Key), keyName, false);
                    item.AddAccelerator("activate", accel, (uint)accelKey, modifier, AccelFlags.Visible);
                }
                catch
                {
                }
            }
            item.Activated += onClick;
            Popup.Append(item);
            Popup.ShowAll();
        }

        /// <summary>
        /// Handle loss of focus by removing the accelerators from the popup menu
        /// </summary>
        /// <param name="o"></param>
        /// <param name="args"></param>
        private void FocusOutEvent(object o, FocusOutEventArgs args)
        {
            ((o as Widget).Toplevel as Gtk.Window).RemoveAccelGroup(accel);
        }

        /// <summary>
        /// Handle receiving focus by adding accelerators for the popup menu
        /// </summary>
        /// <param name="o"></param>
        /// <param name="args"></param>
        private void FocusInEvent(object o, FocusInEventArgs args)
        {
            ((o as Widget).Toplevel as Gtk.Window).AddAccelGroup(accel);
        }

        /// <summary>
        /// Add an action (on context menu) on the series grid.
        /// </summary>
        /// <param name="menuItemText">The text of the menu item</param>
        /// <param name="onClick">The event handler to call when menu is selected</param>
        public void AddContextOption(string menuItemText, System.EventHandler onClick, bool active)
        {
            CheckMenuItem item = new CheckMenuItem(menuItemText);
            item.DrawAsRadio = true;
            item.Active = active;
            item.Activated += onClick;
            Popup.Append(item);
            Popup.ShowAll();
        }

        /// <summary>
        /// Clear all presenter defined context items.
        /// </summary>
        public void ClearContextActions()
        {
            while (Popup.Children.Length > 3)
                Popup.Remove(Popup.Children[3]);
        }

        /// <summary>
        /// Loads an image from a supplied bitmap.
        /// </summary>
        /// <param name="bitmap">The image to display.</param>
        public void LoadImage(Bitmap bitmap)
        {
            imagePixbuf = ImageToPixbuf(bitmap);
            // We should do a better job of rescaling the image. Any ideas?
            double scaleFactor = Math.Min(250.0 / imagePixbuf.Height, 250.0 / imagePixbuf.Width);
            image1.Pixbuf = imagePixbuf.ScaleSimple((int)(imagePixbuf.Width * scaleFactor), (int)(imagePixbuf.Height * scaleFactor), Gdk.InterpType.Bilinear);
            image1.Visible = true;
            scrolledwindow1.HscrollbarPolicy = PolicyType.Never;
        }

        private static Gdk.Pixbuf ImageToPixbuf(System.Drawing.Image image)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Bmp);
                stream.Position = 0;
                Gdk.Pixbuf pixbuf = new Gdk.Pixbuf(stream);
                return pixbuf;
            }
        }
         
        /// <summary>
        /// Loads an image from a manifest resource.
        /// </summary>
        public void LoadImage()
        {
            System.Reflection.Assembly thisExe = System.Reflection.Assembly.GetExecutingAssembly();
            System.IO.Stream file = thisExe.GetManifestResourceStream("ApsimNG.Resources.PresenterPictures." + ModelName + ".png");
            if (file == null)
               image1.Visible = false;
            else
            {
                imagePixbuf = new Gdk.Pixbuf(null, "ApsimNG.Resources.PresenterPictures." + ModelName + ".png");
                // We should do a better job of rescaling the image. Any ideas?
                double scaleFactor = Math.Min(250.0 / imagePixbuf.Height, 250.0 / imagePixbuf.Width);
                image1.Pixbuf = imagePixbuf.ScaleSimple((int)(imagePixbuf.Width * scaleFactor), (int)(imagePixbuf.Height * scaleFactor), Gdk.InterpType.Bilinear);
                image1.Visible = true;
                scrolledwindow1.HscrollbarPolicy = PolicyType.Never;
            }
        }

        /// <summary>
        /// Returns true if the grid row is empty.
        /// </summary>
        /// <param name="rowIndex">The row index</param>
        /// <returns>True if the row is empty</returns>
        public bool RowIsEmpty(int rowIndex)
        {
            // What should we look at here? "DataSource" or "gridmodel"
            // They should be synchronized, but....
            // The Windows.Forms version looked at the grid data, so let's do the same here.
            TreeIter iter;
            if (gridmodel.IterNthChild(out iter, rowIndex))
            {
                for (int i = 0; i < gridmodel.NColumns; i++)
                {
                    string contents = gridmodel.GetValue(iter, i) as string;
                    if (!String.IsNullOrEmpty(contents))
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// End the user editing the cell.
        /// </summary>
        public void EndEdit()
        {
            if (userEditingCell)
            {
                // NB - this assumes that the editing control is a Gtk.Entry control
                // This may change in future versions of Gtk
                if (editControl != null && editControl is Entry)
                {
                    string text = (editControl as Entry).Text;
                    EditedArgs args = new EditedArgs();
                    args.Args = new object[2];
                    args.Args[0] = editPath; // Path
                    args.Args[1] = text;     // NewText
                    OnCellValueChanged(editSender, args);
                }
                else // A fallback procedure
                    ViewBase.SendKeyEvent(gridview, Gdk.Key.Return);
            }
        }

        /// <summary>Lock the left most number of columns.</summary>
        /// <param name="number"></param>
        public void LockLeftMostColumns(int number)
        {
            if (number == numberLockedCols || !gridview.IsMapped)
                return;
            for (int i = 0; i < gridmodel.NColumns; i++)
            {
                if (fixedcolview.Columns.Length > i)
                   fixedcolview.Columns[i].Visible = i < number;
                if (gridview.Columns.Length > i)
                    gridview.Columns[i].Visible = i >= number;
            }
            if (number > 0)
            {
                if (numberLockedCols == 0)
                {
                    gridview.Vadjustment.ValueChanged += Gridview_Vadjustment_Changed;
                    gridview.Selection.Changed += Gridview_CursorChanged;
                    fixedcolview.Vadjustment.ValueChanged += Fixedcolview_Vadjustment_Changed1;
                    fixedcolview.Selection.Changed += Fixedcolview_CursorChanged;
                    Gridview_CursorChanged(this, EventArgs.Empty);
                    Gridview_Vadjustment_Changed(this, EventArgs.Empty);
                }
                fixedcolview.Model = gridmodel;
                fixedcolview.Visible = true;
            }
            else
            {
                gridview.Vadjustment.ValueChanged -= Gridview_Vadjustment_Changed;
                gridview.Selection.Changed -= Gridview_CursorChanged;
                fixedcolview.Vadjustment.ValueChanged -= Fixedcolview_Vadjustment_Changed1;
                fixedcolview.Selection.Changed -= Fixedcolview_CursorChanged;
                fixedcolview.Visible = false;
            }
            numberLockedCols = number;
        }

        /// <summary>Get screenshot of grid.</summary>
        public System.Drawing.Image GetScreenshot()
        {
            // Create a Bitmap and draw the DataGridView on it.
            int width;
            int height;
            Gdk.Window gridWindow = hbox1.GdkWindow;  // Should we draw from hbox1 or from gridview?
            gridWindow.GetSize(out width, out height);
            Gdk.Pixbuf screenshot = Gdk.Pixbuf.FromDrawable(gridWindow, gridWindow.Colormap, 0, 0, 0, 0, width, height);
            byte[] buffer = screenshot.SaveToBuffer("png");
            MemoryStream stream = new MemoryStream(buffer);
            System.Drawing.Bitmap bitmap = new Bitmap(stream);
            return bitmap;
        }

        /// <summary>
        /// The edit control currently in use (if any).
        /// We keep track of this to facilitate handling "partial" edits (e.g., when the user moves to a different component
        /// </summary>
        private CellEditable editControl = null;

        /// <summary>
        /// The tree path for the row currently being edited
        /// </summary>
        private string editPath;

        /// <summary>
        /// The widget which sent the EditingStarted event
        /// </summary>
        private object editSender;

        /// <summary>
        /// User is about to edit a cell.
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnCellBeginEdit(object sender, EditingStartedArgs e)
        {
            this.userEditingCell = true;
            this.editPath = e.Path;
            this.editControl = e.Editable;
            (this.editControl as Widget).KeyPressEvent += Gridview_KeyPressEvent;
            (this.editControl as Widget).FocusOutEvent += GridViewCell_FocusOutEvent;
            this.editSender = sender;
            IGridCell where = GetCurrentCell;
            if (where.RowIndex >= DataSource.Rows.Count)
            {
                for (int i = DataSource.Rows.Count; i <= where.RowIndex; i++)
                {
                    DataRow row = DataSource.NewRow();
                    DataSource.Rows.Add(row);
                }
            }
            this.valueBeforeEdit = this.DataSource.Rows[where.RowIndex][where.ColumnIndex];
            Type dataType = this.valueBeforeEdit.GetType();
            if (dataType == typeof(DateTime))
            {
                Dialog dialog = new Dialog("Select date", gridview.Toplevel as Window, DialogFlags.DestroyWithParent);
                dialog.SetPosition(WindowPosition.None);
                VBox topArea = dialog.VBox;
                topArea.PackStart(new HBox());
                Calendar calendar = new Calendar();
                calendar.DisplayOptions = CalendarDisplayOptions.ShowHeading |
                                     CalendarDisplayOptions.ShowDayNames |
                                     CalendarDisplayOptions.ShowWeekNumbers;
                calendar.Date = (DateTime)this.valueBeforeEdit;
                topArea.PackStart(calendar, true, true, 0);
                dialog.ShowAll();
                dialog.Run();
                // What SHOULD we do here? For now, assume that if the user modified the date in the calendar dialog,
                // the resulting date is what they want. Otherwise, keep the text-editing (Entry) widget active, and
                // let the user enter a value manually.
                if (calendar.Date != (DateTime)this.valueBeforeEdit)
                {
                    DateTime date = calendar.GetDate();
                    this.DataSource.Rows[where.RowIndex][where.ColumnIndex] = date;
                    CellRendererText render = sender as CellRendererText;
                    if (render != null)
                    {
                        render.Text = String.Format("{0:d}", date);
                        if (e.Editable is Entry)
                        {
                            (e.Editable as Entry).Text = render.Text;
                            (e.Editable as Entry).Destroy();
                            this.userEditingCell = false;
                            if (this.CellsChanged != null)
                            {
                                GridCellsChangedArgs args = new GridCellsChangedArgs();
                                args.ChangedCells = new List<IGridCell>();
                                args.ChangedCells.Add(this.GetCell(where.ColumnIndex, where.RowIndex));
                                this.CellsChanged(this, args);
                            }
                        }
                    }
                }
                dialog.Destroy();
            }
        }

        private void ToggleRender_Toggled(object sender, ToggledArgs r)
        {
            IGridCell where = GetCurrentCell;
            while (this.DataSource != null && where.RowIndex >= this.DataSource.Rows.Count)
            {
                this.DataSource.Rows.Add(this.DataSource.NewRow());
            }
            this.DataSource.Rows[where.RowIndex][where.ColumnIndex] = !(bool)this.DataSource.Rows[where.RowIndex][where.ColumnIndex];
            if (this.CellsChanged != null)
            {
                GridCellsChangedArgs args = new GridCellsChangedArgs();
                args.ChangedCells = new List<IGridCell>();
                args.ChangedCells.Add(this.GetCell(where.ColumnIndex, where.RowIndex));
                this.CellsChanged(this, args);
            }
        }

        private void ComboRender_Edited(object sender, EditedArgs e)
        {
            IGridCell where = GetCurrentCell;
            string newText = e.NewText;
            while (this.DataSource != null && where.RowIndex >= this.DataSource.Rows.Count)
            {
                this.DataSource.Rows.Add(this.DataSource.NewRow());
            }

            // Put the new value into the table on the correct row.
            if (this.DataSource != null)
            {
                string oldtext = AsString(this.DataSource.Rows[where.RowIndex][where.ColumnIndex]);
                if (oldtext != newText && newText != null)
                {
                    try
                    {
                        this.DataSource.Rows[where.RowIndex][where.ColumnIndex] = newText;
                    }
                    catch (Exception)
                    {
                    }

                    if (this.CellsChanged != null)
                    {
                        GridCellsChangedArgs args = new GridCellsChangedArgs();
                        args.ChangedCells = new List<IGridCell>();
                        args.ChangedCells.Add(this.GetCell(where.ColumnIndex, where.RowIndex));
                        this.CellsChanged(this, args);
                    }
                }
            }
        }

        /// <summary>
        /// User has finished editing a cell.
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnCellValueChanged(object sender, EditedArgs e)
        {
            if (this.userEditingCell)
            {
                IGridCell where = GetCurrentCell;
                if (where == null)
                    return;
                 
                object oldValue = this.valueBeforeEdit;
                
                this.userEditingCell = false;

                // Make sure our table has enough rows.
                string newtext = e.NewText;
                object newValue = oldValue;
                bool isInvalid = false;
                if (newtext == null)
                {
                    newValue = DBNull.Value;
                }

                Type dataType = oldValue.GetType();
                if (oldValue == DBNull.Value)
                {
                    if (String.IsNullOrEmpty(newtext))
                        return; // If the old value was null, and we've nothing meaningfull to add, pack up and go home
                    dataType = this.DataSource.Columns[where.ColumnIndex].DataType;
                }
                if (dataType == typeof(string))
                    newValue = newtext;
                else if (dataType == typeof(double))
                {
                    double numval;
                    if (Double.TryParse(newtext, out numval))
                        newValue = numval;
                    else
                    {
                        newValue = Double.NaN;
                        isInvalid = true;
                    }
                }
                else if (dataType == typeof(Single))
                {
                    Single numval;
                    if (Single.TryParse(newtext, out numval))
                        newValue = numval;
                    else
                    {
                        newValue = Single.NaN;
                        isInvalid = true;
                    }
                }
                else if (dataType == typeof(int))
                {
                    int numval;
                    if (int.TryParse(newtext, out numval))
                        newValue = numval;
                    else
                    {
                        newValue = 0;
                        isInvalid = true;
                    }
                }
                else if (dataType == typeof(DateTime))
                {
                    DateTime dateval;
                    if (!DateTime.TryParse(newtext, out dateval))
                        isInvalid = true;
                    newValue = dateval;
                }

                while (this.DataSource != null && where.RowIndex >= this.DataSource.Rows.Count)
                {
                    this.DataSource.Rows.Add(this.DataSource.NewRow());
                }

                // Put the new value into the table on the correct row.
                if (this.DataSource != null)
                {
                    try
                    {
                        this.DataSource.Rows[where.RowIndex][where.ColumnIndex] = newValue;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (this.valueBeforeEdit != null && this.valueBeforeEdit.GetType() == typeof(string) && newValue == null)
                {
                    newValue = string.Empty;
                }

                if (this.CellsChanged != null && this.valueBeforeEdit.ToString() != newValue.ToString())
                {
                    GridCellsChangedArgs args = new GridCellsChangedArgs();
                    args.ChangedCells = new List<IGridCell>();
                    args.ChangedCells.Add(this.GetCell(where.ColumnIndex, where.RowIndex));
                    args.invalidValue = isInvalid;
                    this.CellsChanged(this, args);
                }
            }
        }

        /// <summary>
        /// Called when the window is resized to resize all grid controls.
        /// </summary>
        public void ResizeControls()
        {
            if (gridmodel.NColumns == 0)
                return;

            if (gridmodel.IterNChildren() == 0)
            {
                gridview.Visible = false;
            }
            else
                gridview.Visible = true;
        }

        /// <summary>
        /// Trap any grid data errors, usually as a result of cell values not being
        /// in combo boxes. We'll handle these elsewhere.
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnDataError(object sender, /* TBI DataGridViewDataError */ EventArgs e)
        {
            /// TBI e.Cancel = true;
        }

        /// <summary>
        /// User has clicked a cell. 
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnCellMouseDown(object sender, /* TBI DataGridViewCellMouse */ EventArgs e)
        {
            // Probably not needed in the Gtk implementation
            /*
            if (e.RowIndex == -1)
            {
                if (this.ColumnHeaderClicked != null)
                {
                    GridHeaderClickedArgs args = new GridHeaderClickedArgs();
                    args.Column = this.GetColumn(e.ColumnIndex);
                    args.RightClick = e.Button == System.Windows.Forms.MouseButtons.Right;
                    this.ColumnHeaderClicked.Invoke(this, args);
                }
            }
            else if (this.Grid[e.ColumnIndex, e.RowIndex] is Utility.ColorPickerCell)
            {
                ColorDialog dlg = new ColorDialog();

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    this.userEditingCell = true;
                    this.valueBeforeEdit = this.Grid[e.ColumnIndex, e.RowIndex].Value;
                    this.Grid[e.ColumnIndex, e.RowIndex].Value = dlg.Color.ToArgb();
                }
            }
            */
        }

        /// <summary>
        /// We need to trap the EditingControlShowing event so that we can tweak all combo box
        /// cells to allow the user to edit the contents.
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnEditingControlShowing(object sender, /* TBI DataGridViewEditingControlShowing */ EventArgs e)
        {
            // Probably not needed in the Gtk implementation
            /* TBI
            if (this.Grid.CurrentCell is DataGridViewComboBoxCell)
            {
                DataGridViewComboBoxEditingControl combo = (DataGridViewComboBoxEditingControl)this.Grid.EditingControl;
                combo.DropDownStyle = ComboBoxStyle.DropDown;
            }
            */
        }

        /// <summary>
        /// If the cell being validated is a combo cell then always make sure the cell value 
        /// is in the list of combo items.
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnGridCellValidating(object sender, /* TBI DataGridViewCellValidating */ EventArgs e)
        {
            // Probably not needed in the Gtk implementation
            /* 
            if (this.Grid.CurrentCell is DataGridViewComboBoxCell)
            {
                DataGridViewComboBoxEditingControl combo = (DataGridViewComboBoxEditingControl)this.Grid.EditingControl;
                if (combo != null && !combo.Items.Contains(e.FormattedValue))
                {
                    combo.Items.Add(e.FormattedValue);
                }
            }
            */
        }

        /// <summary>
        /// Paste from clipboard into grid.
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnPasteFromClipboard(object sender, EventArgs e)
        {
            {
                List<IGridCell> cellsChanged = new List<IGridCell>();
                int rowIndex = popupCell.RowIndex;
                int columnIndex = popupCell.ColumnIndex;
                if (this.userEditingCell && this.editControl != null)
                {
                    (editControl as Entry).PasteClipboard();
                    cellsChanged.Add(popupCell);
                }
                else
                {
                    Clipboard cb = MainWidget.GetClipboard(Gdk.Selection.Clipboard);
                    string text = cb.WaitForText();
                    if (text != null)
                    {
                        string[] lines = text.Split('\n');
                        foreach (string line in lines)
                        {
                            if (rowIndex < this.RowCount && line.Length > 0)
                            {
                                string[] words = line.Split('\t');
                                for (int i = 0; i < words.GetLength(0); ++i)
                                {
                                    if (columnIndex + i < this.DataSource.Columns.Count)
                                    {
                                        // Make sure there are enough rows in the data source.
                                        while (this.DataSource.Rows.Count <= rowIndex)
                                        {
                                            this.DataSource.Rows.Add(this.DataSource.NewRow());
                                        }

                                        IGridCell cell = this.GetCell(columnIndex + i, rowIndex);
                                        IGridColumn column = this.GetColumn(columnIndex + i);
                                        if (!column.ReadOnly)
                                        {
                                            try
                                            {
                                                if (cell.Value == null || AsString(cell.Value) != words[i])
                                                {
                                                    // We are pasting a new value for this cell. Put the new
                                                    // value into the cell.
                                                    if (words[i] == string.Empty)
                                                    {
                                                        cell.Value = null;
                                                    }
                                                    else
                                                    {
                                                        cell.Value = Convert.ChangeType(words[i], this.DataSource.Columns[columnIndex + i].DataType);
                                                    }

                                                    // Put a cell into the cells changed member.
                                                    cellsChanged.Add(this.GetCell(columnIndex + i, rowIndex));
                                                }
                                            }
                                            catch (FormatException)
                                            {
                                            }
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }

                                rowIndex++;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }

                // If some cells were changed then send out an event.
                if (cellsChanged.Count > 0 && this.CellsChanged != null)
                {
                    fixedcolview.QueueDraw();
                    gridview.QueueDraw();
                    this.CellsChanged.Invoke(this, new GridCellsChangedArgs() { ChangedCells = cellsChanged });
                }
            }
        }

        /// <summary>
        /// Copy to clipboard
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnCopyToClipboard(object sender, EventArgs e)
        {
            if (this.userEditingCell && this.editControl != null)
            {
                (editControl as Entry).CopyClipboard();
            }
            else
            {
                TreeSelection selection = gridview.Selection;
                if (selection.CountSelectedRows() > 0)
                {
                    StringBuilder buffer = new StringBuilder();
                    int nCols = DataSource != null ? this.DataSource.Columns.Count : 0;
                    TreePath[] selRows = selection.GetSelectedRows();
                    foreach (TreePath row in selRows)
                    {
                        int iRow = row.Indices[0];
                        for (int iCol = 0; iCol < nCols; iCol++)
                        {
                            object dataVal = this.DataSource.Rows[iRow][iCol];
                            buffer.Append(AsString(dataVal));
                            if (iCol == nCols - 1)
                                buffer.Append('\n');
                            else
                                buffer.Append('\t');
                        }
                    }
                    Clipboard cb = MainWidget.GetClipboard(Gdk.Selection.Clipboard);
                    cb.Text = buffer.ToString();
                }
            }
        }

        /// <summary>
        /// Delete was clicked by the user.
        /// </summary>
        /// <param name="sender">The sender of the event</param>
        /// <param name="e">The event arguments</param>
        private void OnDeleteClick(object sender, EventArgs e)
        {
            List<IGridCell> cellsChanged = new List<IGridCell>();
            if (this.userEditingCell && this.editControl != null)
            {
                (editControl as Entry).DeleteSelection();
                cellsChanged.Add(popupCell);
            }
            else
            {
                TreeSelection selection = gridview.Selection;
                if (selection.CountSelectedRows() > 0)
                {
                    int nCols = DataSource != null ? this.DataSource.Columns.Count : 0;
                    TreePath[] selRows = selection.GetSelectedRows();
                    foreach (TreePath row in selRows)
                    {
                        int iRow = row.Indices[0];
                        for (int iCol = 0; iCol < nCols; iCol++)
                        {
                            if (!this.GetColumn(iCol).ReadOnly)
                            {
                                DataSource.Rows[iRow][iCol] = DBNull.Value;
                                cellsChanged.Add(this.GetCell(iCol, iRow));
                            }
                        }
                    }
                }
            }
            // If some cells were changed then send out an event.
            if (cellsChanged.Count > 0 && this.CellsChanged != null)
            {
                fixedcolview.QueueDraw();
                gridview.QueueDraw();
                this.CellsChanged.Invoke(this, new GridCellsChangedArgs() { ChangedCells = cellsChanged });
            }
        }

        private void GridView_Resize(object sender, EventArgs e)
        {
            ResizeControls();
        }

        /// <summary>
        /// User has clicked a "button".
        /// </summary>
        private void PixbufRender_Toggled(object o, ToggledArgs args)

        {
            IGridCell cell = GetCurrentCell;
            if (cell != null && cell.EditorType == EditorTypeEnum.Button)
            {
                GridCellsChangedArgs cellClicked = new GridCellsChangedArgs();
                cellClicked.ChangedCells = new List<IGridCell>();
                cellClicked.ChangedCells.Add(cell);
                ButtonClick(this, cellClicked);
            }
        }

        /// <summary>
        /// This prevents the selection changing when the right mouse button is pressed.
        /// Normally, all we want is to display the popup menu, not change the selection.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        [GLib.ConnectBefore] 
        private void OnButtonDown(object sender, ButtonPressEventArgs e)
        {
            if (e.Event.Button == 3)
            {
                if (this.ColumnHeaderClicked != null)
                {
                    GridHeaderClickedArgs args = new GridHeaderClickedArgs();
                    if (sender is TreeView)
                    {
                        TreePath path;
                        TreeViewColumn column;
                        gridview.GetPathAtPos((int)e.Event.X, (int)e.Event.Y, out path, out column);
                        int iRow = path.Indices[0];
                        int xpos = (int)e.Event.X;
                        int iCol = 0;
                        foreach (Widget child in (sender as TreeView).AllChildren)
                        {
                            if (child.GetType() != (typeof(Gtk.Button)))
                                continue;
                            if (xpos >= child.Allocation.Left && xpos <= child.Allocation.Right)
                                break;
                            iCol++;
                        }
                        args.Column = this.GetColumn(iCol);
                        popupCell = new GridCell(this, iCol, iRow);
                    }
                    args.RightClick = true;
                    this.ColumnHeaderClicked.Invoke(this, args);
                }
                Popup.Popup();
                e.RetVal = true;
            }
        }

        /// <summary>
        /// Gets the Gtk Button which displays a column header
        /// This assumes that we can get access to the Button widgets via the grid's AllChildren
        /// iterator.
        /// </summary>
        /// <param name="colNo">Column number we are looking for</param>
        public Button GetColumnHeaderButton(int colNo, TreeView view = null)
        {
            int i = 0;
            if (view == null)
                view = gridview;
            foreach (Widget widget in view.AllChildren)
            {
                if (widget.GetType() != (typeof(Gtk.Button)))
                    continue;
                else if (i++ == colNo)
                    return widget as Button;
            }
            return null;
        }

        /// <summary>
        /// Gets the Label widget rendering the text in the Gtk Button which displays a column header
        /// This is pretty much a hack, but it works. However, it may break in future versions of Gtk.
        /// This assumes that (a) we can get access to the Button widgets via the grid's AllChildren
        /// iterator, and (b) the Button holds an HBox, which holds an Alignment as its first child,
        /// which in turn holds the Label widget
        /// </summary>
        /// <param name="colNo">Column number we are looking for</param>
        public Label GetColumnHeaderLabel(int colNo, TreeView view = null)
        {
            int i = 0;
            if (view == null)
                view = gridview;
            foreach (Widget widget in view.AllChildren)
            {
                if (widget.GetType() != (typeof(Gtk.Button)))
                    continue;
                else if (i++ == colNo)
                {
                    foreach (Widget child in ((Gtk.Button)widget).AllChildren)
                    {
                        if (child.GetType() != (typeof(Gtk.HBox)))
                            continue;
                        foreach (Widget grandChild in ((Gtk.HBox)child).AllChildren)
                        {
                            if (grandChild.GetType() != (typeof(Gtk.Alignment)))
                                continue;
                            foreach (Widget greatGrandChild in ((Gtk.Alignment)grandChild).AllChildren)
                            {
                                if (greatGrandChild.GetType() != (typeof(Gtk.Label)))
                                    continue;
                                else
                                    return greatGrandChild as Label;
                            }
                        }
                    }
                }
            }
            return null;
        }
    }
}

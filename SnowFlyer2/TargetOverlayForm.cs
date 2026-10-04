using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnowFlyer2
{
    internal sealed class FollowTargetInfo
    {
        public IntPtr Address { get; set; }
        public IntPtr OwnerAddress { get; set; }
        public IntPtr ActionAddress { get; set; }
        public IntPtr FrameAddress { get; set; }
        public IntPtr ChassisBodyAddress { get; set; }
        public IntPtr ChassisBodyVtable { get; set; }
        public string AssetId { get; set; }
        public string BoneName { get; set; }
        public Vector3Display Position { get; set; }
        public bool IsActive { get; set; }
        public string Name { get; set; }

        public override string ToString()
        {
            string marker = IsActive ? " [active]" : string.Empty;
            string label = string.IsNullOrWhiteSpace(AssetId) ? "truck" : AssetId;
            return $"{label,-42} {Position.X,8:F1} {Position.Y,7:F1} {Position.Z,8:F1}{marker}";
        }
    }

    internal struct Vector3Display
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3Display(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    internal sealed class TargetOverlayForm : Form
    {
        private readonly Func<IReadOnlyList<FollowTargetInfo>> _refreshTargets;
        private readonly Action<FollowTargetInfo> _selectTarget;
        private readonly Func<string> _attach;
        private readonly Action<bool> _setFaceTruck;
        private readonly Func<bool> _getFaceTruck;
        private readonly Func<string> _getFollowStatus;
        private readonly Timer _statusTimer = new Timer { Interval = 250 };
        private readonly ListBox _targets = new ListBox();
        private readonly Label _status = new Label();
        private bool _refreshing;
        private string _lastFollowStatus;

        public TargetOverlayForm(
            Func<IReadOnlyList<FollowTargetInfo>> refreshTargets,
            Action<FollowTargetInfo> selectTarget,
            Func<string> attach,
            Action<bool> setFaceTruck,
            Func<bool> getFaceTruck,
            IntPtr gameWindow,
            Func<string> getFollowStatus = null)
        {
            _refreshTargets = refreshTargets;
            _selectTarget = selectTarget;
            _attach = attach;
            _setFaceTruck = setFaceTruck;
            _getFaceTruck = getFaceTruck;
            _getFollowStatus = getFollowStatus;

            Text = "SnowRunner Spectator Targets";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.FromArgb(24, 24, 24);
            ForeColor = Color.White;
            Opacity = 0.94;
            Width = 520;
            Height = 470;
            StartPosition = FormStartPosition.Manual;
            SetOverlayPosition(gameWindow);

            Label title = new Label
            {
                Text = "Truck chassis (validated asset / physics position)",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(10, 0, 0, 0),
                ForeColor = Color.White
            };
            _targets.Dock = DockStyle.Fill;
            _targets.Font = new Font(FontFamily.GenericMonospace, 9.0f);
            _targets.BackColor = Color.FromArgb(40, 40, 40);
            _targets.ForeColor = Color.White;
            _targets.HorizontalScrollbar = true;
            _targets.SelectedIndexChanged += (sender, args) =>
            {
                if (_targets.SelectedItem is FollowTargetInfo item)
                    _selectTarget(item);
            };

            Button refresh = new Button { Text = "Refresh", Width = 90, Height = 28 };
            refresh.Click += (sender, args) => RefreshTargetList();
            Button attachButton = new Button { Text = "Attach / follow", Width = 120, Height = 28 };
            attachButton.Click += (sender, args) =>
            {
                string result = _attach();
                Console.WriteLine("Spectator attach: " + result);
                SetStatus(result);
            };
            Button close = new Button { Text = "Close", Width = 90, Height = 28 };
            close.Click += (sender, args) => Close();
            CheckBox faceTruck = new CheckBox
            {
                Text = "Face truck",
                AutoSize = true,
                Checked = _getFaceTruck != null && _getFaceTruck(),
                ForeColor = Color.White,
                Margin = new Padding(6, 5, 12, 2)
            };
            faceTruck.CheckedChanged += (sender, args) =>
            {
                if (_setFaceTruck != null)
                    _setFaceTruck(faceTruck.Checked);
            };
            Label orbitInstructions = new Label
            {
                Text = "While following: hold right mouse and drag, or use the controller right stick, to move around the truck.",
                AutoSize = false,
                Width = 340,
                Height = 30,
                ForeColor = Color.LightGray,
                Margin = new Padding(4, 2, 4, 2)
            };
            FlowLayoutPanel modeOptions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Height = 48,
                Padding = new Padding(2),
                BackColor = Color.FromArgb(30, 30, 30),
                WrapContents = false
            };
            modeOptions.Controls.Add(faceTruck);
            modeOptions.Controls.Add(orbitInstructions);

            FlowLayoutPanel controls = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                Padding = new Padding(6),
                BackColor = Color.FromArgb(30, 30, 30)
            };
            controls.Controls.Add(refresh);
            controls.Controls.Add(attachButton);
            controls.Controls.Add(close);
            _status.Dock = DockStyle.Bottom;
            _status.Height = 28;
            _status.Padding = new Padding(8, 5, 0, 0);
            _status.ForeColor = Color.LightGray;
            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 92,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            footer.Controls.Add(modeOptions);
            footer.Controls.Add(_status);
            footer.Controls.Add(controls);
            Controls.Add(_targets);
            Controls.Add(footer);
            Controls.Add(title);

            KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Escape)
                {
                    Close();
                    args.Handled = true;
                }
                else if (args.KeyCode == Keys.F5)
                {
                    RefreshTargetList();
                    args.Handled = true;
                }
            };
            Shown += (sender, args) => RefreshTargetList();
            _statusTimer.Tick += (sender, args) =>
            {
                if (!_refreshing && _getFollowStatus != null)
                {
                    string status = _getFollowStatus();
                    if (status != _lastFollowStatus)
                    {
                        _lastFollowStatus = status;
                        SetStatus(status);
                    }
                }
            };
            Shown += (sender, args) => _statusTimer.Start();
            FormClosed += (sender, args) => _statusTimer.Dispose();
        }

        private void SetOverlayPosition(IntPtr gameWindow)
        {
            if (gameWindow != IntPtr.Zero && NativeMethods.GetWindowRect(gameWindow, out NativeMethods.Rect rect))
            {
                int x = rect.Left + 24;
                int y = rect.Top + 48;
                Location = new Point(x, y);
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
        }

        private async void RefreshTargetList()
        {
            if (_refreshing)
                return;
            _refreshing = true;
            IntPtr previousOwner = IntPtr.Zero;
            if (_targets.SelectedItem is FollowTargetInfo previous)
                previousOwner = previous.OwnerAddress;
            try
            {
                SetStatus("Scanning loaded truck chassis...");
                IReadOnlyList<FollowTargetInfo> items =
                    await Task.Run(() => _refreshTargets());
                if (IsDisposed || Disposing)
                    return;
                _targets.BeginUpdate();
                try
                {
                    _targets.Items.Clear();
                    foreach (FollowTargetInfo item in items)
                        _targets.Items.Add(item);
                    if (_targets.Items.Count > 0)
                    {
                        int selected = 0;
                        for (int index = 0; index < _targets.Items.Count; index++)
                        {
                            if (((FollowTargetInfo)_targets.Items[index]).OwnerAddress == previousOwner)
                            {
                                selected = index;
                                break;
                            }
                        }
                        _targets.SelectedIndex = selected;
                    }
                }
                finally
                {
                    _targets.EndUpdate();
                }
                SetStatus($"{items.Count} validated truck chassis. Select one and attach.");
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !Disposing)
                    SetStatus($"Refresh failed: {ex.Message}");
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void SetStatus(string text)
        {
            _status.Text = text ?? string.Empty;
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct Rect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
        }
    }
}

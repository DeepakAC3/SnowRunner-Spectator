using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnowFlyer2
{
    internal sealed class FollowTargetInfo
    {
        public IntPtr Address { get; set; }
        public Vector3Display Position { get; set; }
        public bool IsActive { get; set; }

        public override string ToString()
        {
            string marker = IsActive ? " [active]" : string.Empty;
            return $"{Position.X,8:F1} {Position.Y,7:F1} {Position.Z,8:F1}   0x{Address.ToInt64():X}{marker}";
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
        private readonly ListBox _targets = new ListBox();
        private readonly Label _status = new Label();
        private bool _refreshing;

        public TargetOverlayForm(
            Func<IReadOnlyList<FollowTargetInfo>> refreshTargets,
            Action<FollowTargetInfo> selectTarget,
            Func<string> attach,
            IntPtr gameWindow)
        {
            _refreshTargets = refreshTargets;
            _selectTarget = selectTarget;
            _attach = attach;

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
                Text = "Spectator targets (world position)",
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
            attachButton.Click += (sender, args) => SetStatus(_attach());
            Button close = new Button { Text = "Close", Width = 90, Height = 28 };
            close.Click += (sender, args) => Close();
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
            Controls.Add(_targets);
            Controls.Add(_status);
            Controls.Add(controls);
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
            IntPtr previousAddress = IntPtr.Zero;
            if (_targets.SelectedItem is FollowTargetInfo previous)
                previousAddress = previous.Address;
            try
            {
                SetStatus("Scanning transform candidates...");
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
                            if (((FollowTargetInfo)_targets.Items[index]).Address == previousAddress)
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
                SetStatus($"{items.Count} transform candidates. Select one and attach.");
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

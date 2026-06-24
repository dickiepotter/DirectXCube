using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WinFormsPanel = System.Windows.Forms.Panel;

namespace DirectXCube;

/// <summary>
/// Hosts a Direct3D 11 swap chain inside a WPF window. A <see cref="WindowsFormsHost"/> supplies a
/// real Win32 child window (a WinForms <see cref="WinFormsPanel"/>) for Direct3D to render into, and
/// <see cref="CompositionTarget.Rendering"/> drives one frame per WPF composition tick.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CubeRenderer _renderer = new();
    private readonly WinFormsPanel _panel = new() { Dock = System.Windows.Forms.DockStyle.Fill };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _initialized;

    // Optional "--capture <path>" support: render a few frames, save a PNG, then exit.
    private string? _capturePath;
    private int _frameCount;

    public MainWindow()
    {
        InitializeComponent();

        _capturePath = ScreenCapture.PathFrom(Environment.GetCommandLineArgs());

        Host.Child = _panel;
        Host.Loaded += OnHostLoaded;
        SizeChanged += (_, _) => _renderer.Resize(_panel.ClientSize.Width, _panel.ClientSize.Height);
        Closed += (_, _) => { CompositionTarget.Rendering -= OnRendering; _renderer.Dispose(); };
    }

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _renderer.Initialize(_panel.Handle, _panel.ClientSize.Width, _panel.ClientSize.Height);
        _initialized = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _renderer.Render((float)_clock.Elapsed.TotalSeconds);

        if (_capturePath is null)
            return;

        // Let a handful of frames go by so the GPU has something settled to capture.
        if (++_frameCount < 5)
            return;

        _renderer.CaptureToPng(_capturePath);
        _capturePath = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
    }
}

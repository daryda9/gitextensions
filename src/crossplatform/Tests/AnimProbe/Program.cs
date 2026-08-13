using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Views;

// Does BusyOverlay's spinner actually turn?
//
// It went frozen once already and nothing said so: Animation.RunAsync on a
// RotateTransform throws InvalidCastException (Avalonia's TransformAnimator casts its
// target to Visual), and the throw was swallowed by a fire-and-forget continuation.
// A spinner that does not spin looks like an app that has hung, so it is worth a
// check that fails loudly.
//
// Samples the RotateTransform the control rotates. Needs a real window: the frames
// only tick against a live render loop. Exit code 0 = spinning, 1 = frozen.

AppBuilder.Configure<GitExtensions.Avalonia.App>()
    .UsePlatformDetect()
    .SetupWithoutStarting();

BusyOverlay overlay = new() { Delay = TimeSpan.Zero };

Window window = new()
{
    Width = 200,
    Height = 200,
    Title = "spinner probe",
    Content = new Panel { Children = { overlay } },
};

RotateTransform rotation = (RotateTransform)typeof(BusyOverlay)
    .GetField("_rotation", BindingFlags.Instance | BindingFlags.NonPublic)!
    .GetValue(overlay)!;

List<double> samples = [];

window.Opened += (_, _) =>
{
    overlay.Show();

    DispatcherTimer sampler = new() { Interval = TimeSpan.FromMilliseconds(120) };
    sampler.Tick += (_, _) =>
    {
        samples.Add(rotation.Angle);
        if (samples.Count < 14)
        {
            return;
        }

        sampler.Stop();

        int distinct = samples.Distinct().Count();
        bool spinning = distinct > 2;

        Console.WriteLine("visible:  " + overlay.IsVisible);
        Console.WriteLine("angles:   " + string.Join(" ", samples.Take(8).Select(a => a.ToString("0.0"))));
        Console.WriteLine("distinct: " + distinct);

        // Hide must also stop it: a timer left ticking on a collapsed overlay is the
        // battery drain the control's own documentation promises not to cause.
        overlay.Hide();
        double afterHide = rotation.Angle;
        Dispatcher.UIThread.Post(() =>
        {
            bool stopped = Math.Abs(rotation.Angle - afterHide) < 0.001 && rotation.Angle == 0;
            Console.WriteLine("after Hide: angle=" + rotation.Angle.ToString("0.0") + (stopped ? " (stopped, reset)" : " (STILL RUNNING)"));
            Console.WriteLine(spinning && stopped ? "RESULT: OK" : "RESULT: FAILED");
            Console.Out.Flush();
            window.Close();
            Environment.Exit(spinning && stopped ? 0 : 1);
        }, DispatcherPriority.Background);
    };
    sampler.Start();
};

window.Show();
Dispatcher.UIThread.MainLoop(CancellationToken.None);

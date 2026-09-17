using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace G9SyncData.DeviceTests;

/// <summary>
///     Entry point of one device run. The host starts it with <c>am start -S -W -n com.g9tm.g9syncdata.devicetests/.MainActivity
///     --es run &lt;id&gt; --es scenario unit|apply|sync …</c>, then polls <c>files/runs/&lt;id&gt;/status</c> in the app's external files
///     directory (readable over adb without a debuggable build). The work runs off the UI thread; the activity stays in the foreground
///     so the UI-lag probe measures what a user would feel.
/// </summary>
[Activity(Name = "com.g9tm.g9syncdata.devicetests.MainActivity", Label = "G9SyncData device tests", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private TextView? _text;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        _text = new TextView(this) { TextSize = 11 };
        var scroll = new ScrollView(this);
        scroll.AddView(_text);
        SetContentView(scroll);

        SQLitePCL.Batteries_V2.Init();

        var extras = DeviceRunArguments.From(Intent);
        var root = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        var context = new DeviceRunContext(this, extras, Path.Combine(root, "runs", extras.RunId), FilesDir!.AbsolutePath, Append);
        var probe = UiLagProbe.Start();
        _ = Task.Run(() => DeviceRunner.RunAsync(context, probe));
    }

    private void Append(string line) => RunOnUiThread(() =>
    {
        if (_text is null) return;
        _text.Append(line + "\n");
        if (_text.Length() > 20_000) _text.Text = _text.Text![^10_000..];
    });
}

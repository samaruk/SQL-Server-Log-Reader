using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace BonikPOS.VoiceSearch.AI.Onnx;

/// <summary>
/// Turns off ONNX Runtime's built-in usage telemetry.
/// </summary>
/// <remarks>
/// The Microsoft.ML.OnnxRuntime 1.31.0 binaries for Android, iOS and Linux contain the Microsoft 1DS telemetry client
/// (collector https://mobile.events.data.microsoft.com/OneCollector/1.0; see onnxruntime/core/platform/telemetry_1ds.cc).
/// This app must not send data off the phone without explicit configuration, so telemetry is disabled before the first
/// ONNX Runtime environment is created:
/// <list type="number">
/// <item>ORT reads <c>ORT_DISABLE_TELEMETRY</c> from the native process environment when its telemetry provider starts
/// and latches the opt-out for the whole process. .NET's <see cref="Environment.SetEnvironmentVariable(string, string)"/>
/// does not change the native environment on Unix, so <c>setenv</c> is called directly.</item>
/// <item><see cref="OrtEnv.DisableTelemetryEvents"/> is called as a second, API-level opt-out.</item>
/// <item>On Android the AAR's <c>ai.onnxruntime.TelemetryInitializer</c> content provider is also removed from the
/// merged manifest (Platforms/Android/AndroidManifest.xml).</item>
/// </list>
/// </remarks>
public static partial class OnnxRuntimeTelemetry
{
    public const string EnvironmentVariable = "ORT_DISABLE_TELEMETRY";

    private static readonly Lock Gate = new();
    private static bool _disabled;

    /// <summary>True when the native environment variable was set before ONNX Runtime started.</summary>
    public static bool NativeFlagSet { get; private set; }

    /// <summary>Idempotent. Call before any ONNX Runtime session or environment is created.</summary>
    public static void Disable()
    {
        lock (Gate)
        {
            if (_disabled)
            {
                return;
            }

            Environment.SetEnvironmentVariable(EnvironmentVariable, "1"); // Windows reads the process environment block
            NativeFlagSet = OperatingSystem.IsWindows() || TrySetNative(EnvironmentVariable, "1");
            OrtEnv.Instance().DisableTelemetryEvents();
            _disabled = true;
        }
    }

    private static bool TrySetNative(string name, string value)
    {
        try
        {
            return SetEnv(name, value, 1) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);
}

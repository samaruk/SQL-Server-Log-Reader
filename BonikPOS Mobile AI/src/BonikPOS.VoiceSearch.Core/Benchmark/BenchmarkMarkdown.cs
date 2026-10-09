using System.Globalization;
using System.Text;

namespace BonikPOS.VoiceSearch.Core.Benchmark;

/// <summary>Human-readable rendering of a <see cref="PipelineBenchmarkReport"/> (Markdown tables).</summary>
public static class BenchmarkMarkdown
{
    public static string Render(PipelineBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"### Pipeline benchmark{(string.IsNullOrWhiteSpace(report.Label) ? string.Empty : ": " + report.Label)}");
        sb.AppendLine();
        sb.AppendLine(inv, $"- MEASURED on: {report.Device} ({report.LogicalProcessors} logical CPUs), {report.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine(inv, $"- Options: {report.Options.Repetitions} repetitions, {(report.Options.RealTime ? "real-time replay" : "fast replay")}, warm-up {(report.Options.WarmUp ? "on" : "off")}, verification {(report.Options.ParallelVerification ? "parallel with recognition" : "before recognition")}");
        foreach (var note in report.Notes)
        {
            sb.AppendLine(inv, $"- Note: {note}");
        }

        foreach (var run in report.Runs)
        {
            sb.AppendLine();
            sb.AppendLine(inv, $"#### {run.Accelerator}");
            sb.AppendLine();
            sb.AppendLine("| Model role | Model | Execution provider | Init ms |");
            sb.AppendLine("|---|---|---|---:|");
            foreach (var m in run.Models)
            {
                sb.AppendLine(inv, $"| {m.Role} | {(m.Available ? m.ModelId : "not installed")} | {m.ExecutionProvider ?? "-"} | {Fmt(m.InitMilliseconds)} |");
            }

            sb.AppendLine();
            sb.AppendLine(inv, $"Warm-up {run.WarmUpMs:0} ms; wall time {run.WallSeconds:0.0} s; frames captured {run.FramesCaptured}, dropped {run.FramesDropped}, discontinuities {run.Discontinuities}, recognition-queue drops {run.RecognitionQueueDrops}.");
            sb.AppendLine(inv, $"Process CPU {Fmt(run.ProcessCpuPercentOfOneCore)} % of one core ({Fmt(run.ProcessCpuPercentOfDevice)} % of all cores); peak managed memory {run.PeakManagedMb:0} MB; peak working set {Fmt(run.PeakWorkingSetMb)} MB.");
            if (run.Before.BatteryLevel is not null || run.Before.ThermalState is not null)
            {
                sb.AppendLine(inv, $"Battery {Fmt(run.Before.BatteryLevel * 100)} % -> {Fmt(run.After.BatteryLevel * 100)} % ({run.After.BatteryState}); thermal {run.Before.ThermalState} -> {run.After.ThermalState}; " +
                    $"battery temperature {Fmt1(run.Before.BatteryTemperatureCelsius)} -> {Fmt1(run.After.BatteryTemperatureCelsius)} C (max {Fmt1(run.MaxBatteryTemperatureCelsius)}); " +
                    $"mean battery current {Fmt(run.MeanBatteryCurrentMicroamps / 1000)} mA; mean whole-device battery power {Fmt(run.MeanBatteryPowerMilliwatts)} mW.");
            }

            sb.AppendLine();
            sb.AppendLine("| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var m in run.Metrics.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                sb.AppendLine(inv, $"| {m.Name} | {m.Count} | {m.P50:0.0} | {m.P95:0.0} | {m.Max:0.0} | {m.Mean:0.0} |");
            }

            sb.AppendLine();
            sb.AppendLine("| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |");
            sb.AppendLine("|---|---:|---|---:|---:|---|---:|---:|");
            foreach (var c in run.Clips)
            {
                string asr = c.AsrMs is null && c.AsrEncoderMs is null ? "-" :
                    $"{Fmt(c.AsrMs)} ({Fmt(c.AsrEncoderMs)} / {Fmt(c.AsrDecoderMs)}, {c.AsrTokens})";
                sb.AppendLine(inv, $"| {c.Clip} | {c.Repetition} | {c.Outcome} | {c.SpeechMs:0} | {Fmt(c.VerifyMs)} | {asr} | {Fmt(c.SearchMs)} | {Fmt(c.EndOfSpeechToOutcomeMs ?? c.EndOfSpeechToResultMs)} |");
            }
        }

        return sb.ToString();

        static string Fmt(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0", CultureInfo.InvariantCulture) : "-";

        static string Fmt1(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0.0", CultureInfo.InvariantCulture) : "-";
    }
}

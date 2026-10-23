using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;

namespace ThermoTray.Sensors;

/// <summary>
/// ThermoTray 感測器 sidecar。由 Rust 主程式啟動並透過 stdin/stdout 以單行文字協定溝通：
/// <list type="bullet">
/// <item><c>READ</c>：更新所有硬體並回傳一行 JSON 原始感測器樹（不做任何挑選或估算）。</item>
/// <item><c>DRIVER</c>：回傳 PawnIO 驅動程式安裝狀態。</item>
/// <item><c>EXIT</c> 或 stdin 關閉：關閉 LibreHardwareMonitor 並結束。</item>
/// </list>
/// 感測器挑選、排序與 GPU 配對全部在 Rust 端完成，這裡只負責忠實轉送 LibreHardwareMonitor 的讀值。
/// </summary>
internal static class Program
{
    private static readonly UpdateVisitor HardwareUpdater = new();

    private static int Main()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = Console.OpenStandardOutput();
        var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
        var opened = false;

        try
        {
            string? request;
            while ((request = input.ReadLine()) is not null)
            {
                switch (request.Trim())
                {
                    case "READ":
                        WriteLine(output, writer =>
                        {
                            try
                            {
                                if (!opened)
                                {
                                    computer.Open();
                                    opened = true;
                                }

                                computer.Accept(HardwareUpdater);
                                WriteTree(writer, computer);
                            }
                            catch (Exception exception)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("error", exception.GetType().Name + ": " + exception.Message);
                                writer.WriteEndObject();
                            }
                        });
                        break;

                    case "DRIVER":
                        WriteLine(output, WriteDriverStatus);
                        break;

                    case "EXIT":
                        return 0;

                    default:
                        WriteLine(output, writer =>
                        {
                            writer.WriteStartObject();
                            writer.WriteString("error", "unknown request");
                            writer.WriteEndObject();
                        });
                        break;
                }
            }

            return 0;
        }
        catch (IOException)
        {
            // 主程式結束時管道會中斷，這是正常的關閉路徑
            return 0;
        }
        finally
        {
            if (opened)
            {
                computer.Close();
            }
        }
    }

    /// <summary>
    /// 寫出一行 JSON 回應並立即排清，讓主程式可以逐行讀取。
    /// </summary>
    private static void WriteLine(Stream output, Action<Utf8JsonWriter> write)
    {
        using (var writer = new Utf8JsonWriter(output))
        {
            write(writer);
        }

        output.WriteByte((byte)'\n');
        output.Flush();
    }

    private static void WriteTree(Utf8JsonWriter writer, IComputer computer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("hardware");
        foreach (var hardware in computer.Hardware)
        {
            WriteHardware(writer, hardware);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteHardware(Utf8JsonWriter writer, IHardware hardware)
    {
        writer.WriteStartObject();
        writer.WriteString("type", hardware.HardwareType.ToString());
        writer.WriteString("name", hardware.Name);
        writer.WriteStartArray("sensors");
        foreach (var sensor in hardware.Sensors)
        {
            // 停用歷史紀錄視窗，避免長時間執行時清單持續成長
            if (sensor.ValuesTimeWindow != TimeSpan.Zero)
            {
                sensor.ValuesTimeWindow = TimeSpan.Zero;
            }

            if (sensor.SensorType is not (SensorType.Temperature or SensorType.Load))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("type", sensor.SensorType.ToString());
            writer.WriteString("name", sensor.Name);
            if (sensor.Value is float value && float.IsFinite(value))
            {
                writer.WriteNumber("value", value);
            }
            else
            {
                writer.WriteNull("value");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("sub");
        foreach (var subHardware in hardware.SubHardware)
        {
            WriteHardware(writer, subHardware);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDriverStatus(Utf8JsonWriter writer)
    {
        var installed = false;
        var version = string.Empty;
        try
        {
            installed = PawnIo.IsInstalled;
            version = installed ? PawnIo.Version?.ToString() ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            // 探測失敗一律視為未安裝，不假設驅動程式可用
            installed = false;
            version = string.Empty;
        }

        writer.WriteStartObject();
        writer.WriteBoolean("installed", installed);
        writer.WriteString("version", version);
        writer.WriteEndObject();
    }

    /// <summary>
    /// 觸發每個硬體與子硬體的 <c>Update()</c>。
    /// </summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}

using System.Collections;
using System.Text.RegularExpressions;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Tests.TestSupport;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Daqifi.Core.Tests.Communication.Messages;

/// <summary>
/// Pins what regenerating <c>DaqifiOutMessage.cs</c> must not change - field numbers and wire
/// types, as firmware 3.5.0 and later sends them - and that the checked-in <c>.proto</c> is the
/// one the generated code came from.
/// </summary>
public class DaqifiOutMessageWireFormatTests
{
    public static readonly TheoryData<int, string, FieldType, bool> Fields = new()
    {
        { 1, "msg_time_stamp", FieldType.UInt32, false },
        { 2, "analog_in_data", FieldType.SInt32, true },
        { 3, "analog_in_data_float", FieldType.Float, true },
        { 4, "analog_in_data_ts", FieldType.UInt32, true },
        { 5, "digital_data", FieldType.Bytes, false },
        { 6, "digital_data_ts", FieldType.UInt32, true },
        { 7, "analog_out_data", FieldType.UInt32, true },
        { 8, "device_status", FieldType.UInt32, false },
        { 9, "pwr_status", FieldType.UInt32, false },
        { 10, "batt_status", FieldType.UInt32, false },
        { 11, "temp_status", FieldType.SInt32, false },
        { 16, "timestamp_freq", FieldType.UInt32, false },
        { 17, "analog_in_port_num", FieldType.UInt32, false },
        { 18, "analog_in_port_num_priv", FieldType.UInt32, false },
        { 19, "analog_in_port_type", FieldType.Bytes, false },
        { 20, "analog_in_port_av_rse", FieldType.Bytes, false },
        { 21, "analog_in_port_rse", FieldType.Bytes, false },
        { 22, "analog_in_port_enabled", FieldType.Bytes, false },
        { 23, "analog_in_port_av_range", FieldType.Float, true },
        { 24, "analog_in_port_av_range_priv", FieldType.Float, true },
        { 25, "analog_in_port_range", FieldType.Float, true },
        { 26, "analog_in_port_range_priv", FieldType.Float, true },
        { 27, "analog_in_res", FieldType.UInt32, false },
        { 28, "analog_in_res_priv", FieldType.UInt32, false },
        { 29, "analog_in_int_scale_m", FieldType.Float, true },
        { 30, "analog_in_int_scale_m_priv", FieldType.Float, true },
        { 31, "analog_in_cal_m", FieldType.Float, true },
        { 32, "analog_in_cal_b", FieldType.Float, true },
        { 33, "analog_in_cal_m_priv", FieldType.Float, true },
        { 34, "analog_in_cal_b_priv", FieldType.Float, true },
        { 35, "digital_port_num", FieldType.UInt32, false },
        { 36, "digital_port_type", FieldType.Bytes, false },
        { 37, "digital_port_dir", FieldType.Bytes, false },
        { 38, "analog_out_port_num", FieldType.UInt32, false },
        { 39, "analog_out_port_type", FieldType.Bytes, false },
        { 40, "analog_out_res", FieldType.UInt32, false },
        { 41, "analog_out_port_av_range", FieldType.Float, true },
        { 42, "analog_out_port_range", FieldType.Float, false },
        { 43, "ip_addr", FieldType.Bytes, false },
        { 44, "net_mask", FieldType.Bytes, false },
        { 45, "gateway", FieldType.Bytes, false },
        { 46, "primary_dns", FieldType.Bytes, false },
        { 47, "secondary_dns", FieldType.Bytes, false },
        { 48, "mac_addr", FieldType.Bytes, false },
        { 49, "ip_addr_v6", FieldType.Bytes, false },
        { 50, "sub_pre_length_v6", FieldType.Bytes, false },
        { 51, "gateway_v6", FieldType.Bytes, false },
        { 52, "primary_dns_v6", FieldType.Bytes, false },
        { 53, "secondary_dns_v6", FieldType.Bytes, false },
        { 54, "eui_64", FieldType.Bytes, false },
        { 55, "host_name", FieldType.String, false },
        { 56, "device_port", FieldType.UInt32, false },
        { 57, "friendly_device_name", FieldType.String, false },
        { 58, "ssid", FieldType.String, false },
        { 59, "ssid_strength", FieldType.UInt32, false },
        { 60, "wifi_security_mode", FieldType.UInt32, false },
        { 61, "wifi_inf_mode", FieldType.UInt32, false },
        { 62, "av_ssid", FieldType.String, true },
        { 63, "av_ssid_strength", FieldType.UInt32, true },
        { 64, "av_wifi_security_mode", FieldType.UInt32, true },
        { 65, "av_wifi_inf_mode", FieldType.UInt32, true },
        { 66, "device_pn", FieldType.String, false },
        { 67, "device_hw_rev", FieldType.String, false },
        { 68, "device_fw_rev", FieldType.String, false },
        { 69, "device_sn", FieldType.UInt64, false },
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void Field_EncodedByHand_DecodesIntoTheSameProperty(
        int number, string name, FieldType type, bool repeated)
    {
        // Two values for a repeated field, so a property that stopped being repeated (and kept
        // only the last one) cannot pass.
        var expected = repeated ? new[] { Sample(type, 1), Sample(type, 2) } : [Sample(type, 1)];

        var message = DaqifiOutMessage.Parser.ParseFrom(Encode(number, type, repeated, expected));

        var field = DaqifiOutMessage.Descriptor.FindFieldByNumber(number);
        Assert.NotNull(field);
        Assert.Equal(name, field.Name);
        Assert.Equal(type, field.FieldType);
        Assert.Equal(repeated, field.IsRepeated);

        var value = typeof(DaqifiOutMessage).GetProperty(PropertyNameOf(name))!.GetValue(message);
        var actual = repeated ? ((IEnumerable)value!).Cast<object>().ToArray() : [value!];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Descriptor_HasTheNamespaceAndTheSameFileOptionsAsBefore()
    {
        var file = DaqifiOutMessageReflection.Descriptor;

        Assert.Equal("Daqifi.Core.Communication.Messages", typeof(DaqifiOutMessage).Namespace);
        Assert.Equal("Daqifi.Core.Communication.Messages", file.GetOptions().CsharpNamespace);

        // The C# namespace is not the protobuf package: the package is part of the wire-level type
        // name, the firmware declares none, and it has to stay that way.
        Assert.Equal("DaqifiOutMessage", DaqifiOutMessage.Descriptor.FullName);
        Assert.Equal("Protos/DaqifiOutMessage.proto", file.Name);
        Assert.Equal("com.daqifi.common.messages", file.GetOptions().JavaPackage);
        Assert.Equal("ProtoMessageV2", file.GetOptions().JavaOuterClassname);
        Assert.Equal(Google.Protobuf.Reflection.FileOptions.Types.OptimizeMode.LiteRuntime, file.GetOptions().OptimizeFor);
    }

    [Fact]
    public void CheckedInProto_DeclaresExactlyTheFieldsInTheGeneratedCode()
    {
        var proto = File.ReadAllText(
            Path.Combine(RepositoryRoot.FullPath, "src", "Daqifi.Core", "Protos", "DaqifiOutMessage.proto"));

        var declared = Regex
            .Matches(proto, @"^\s*(repeated\s+)?(\w+)\s+(\w+)\s*=\s*(\d+)\s*;", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[4].Value} {m.Groups[3].Value} {m.Groups[2].Value} {m.Groups[1].Success}")
            .ToList();

        var generated = DaqifiOutMessage.Descriptor.Fields.InDeclarationOrder()
            .Select(f => $"{f.FieldNumber} {f.Name} {f.FieldType.ToString().ToLowerInvariant()} {f.IsRepeated}")
            .ToList();

        Assert.Equal(generated, declared);
    }

    private static object Sample(FieldType type, int seed) => type switch
    {
        // Large enough to need every byte of the varint, so a narrower type would truncate it.
        FieldType.UInt32 => uint.MaxValue - (uint)seed,
        FieldType.UInt64 => ulong.MaxValue - (ulong)seed,
        // Negative: zigzag (sint32) and plain int32 encodings only agree on non-negative values
        // by accident, never on these.
        FieldType.SInt32 => -100_000 - seed,
        FieldType.Float => -1.5f * seed,
        FieldType.Bytes => ByteString.CopyFrom(0xFF, 0x00, (byte)seed),
        FieldType.String => $"DAQiFi-{seed}",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No sample for this type."),
    };

    private static byte[] Encode(int number, FieldType type, bool repeated, object[] values)
    {
        using var buffer = new MemoryStream();
        using (var output = new CodedOutputStream(buffer, leaveOpen: true))
        {
            // proto3 packs repeated numeric fields into one length-delimited run, and the
            // firmware sends them that way. Strings and bytes are never packed.
            if (repeated && type is not (FieldType.String or FieldType.Bytes))
            {
                using var run = new MemoryStream();
                using (var packed = new CodedOutputStream(run, leaveOpen: true))
                {
                    foreach (var value in values)
                    {
                        WriteValue(packed, type, value);
                    }
                }

                output.WriteTag(number, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(run.ToArray()));
            }
            else
            {
                foreach (var value in values)
                {
                    output.WriteTag(number, WireTypeOf(type));
                    WriteValue(output, type, value);
                }
            }
        }

        return buffer.ToArray();
    }

    private static WireFormat.WireType WireTypeOf(FieldType type) => type switch
    {
        FieldType.Float => WireFormat.WireType.Fixed32,
        FieldType.String or FieldType.Bytes => WireFormat.WireType.LengthDelimited,
        _ => WireFormat.WireType.Varint,
    };

    private static void WriteValue(CodedOutputStream output, FieldType type, object value)
    {
        switch (type)
        {
            case FieldType.UInt32: output.WriteUInt32((uint)value); break;
            case FieldType.UInt64: output.WriteUInt64((ulong)value); break;
            case FieldType.SInt32: output.WriteSInt32((int)value); break;
            case FieldType.Float: output.WriteFloat((float)value); break;
            case FieldType.Bytes: output.WriteBytes((ByteString)value); break;
            case FieldType.String: output.WriteString((string)value); break;
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "No writer for this type.");
        }
    }

    private static string PropertyNameOf(string protoName) =>
        string.Concat(protoName.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

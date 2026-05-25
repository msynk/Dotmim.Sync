using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dotmim.Sync.Serialization
{
    /// <summary>
    /// Convert an object to an inferred type (string, bool, long, double or datetimeoffset).
    /// </summary>
    public class ObjectToInferredTypesConverter : JsonConverter<object>
    {
        /// <summary>
        /// Read the next value and infer the type between string, bool, long, double or datetimeoffset.
        /// </summary>
        public static object ReadValue(ref Utf8JsonReader reader)
        {

            object value = null;

            if (reader.TokenType == JsonTokenType.Null || reader.TokenType == JsonTokenType.None)
                value = null;
            else if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var datetimeOffset))
                value = datetimeOffset;
            else if (reader.TokenType == JsonTokenType.String)
                value = reader.GetString();
            else if (reader.TokenType == JsonTokenType.False || reader.TokenType == JsonTokenType.True)
                value = reader.GetBoolean();
            else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var l))
                value = l;
            else if (reader.TokenType == JsonTokenType.Number)
                value = reader.GetDouble();

            return value;
        }

        /// <summary>
        /// Write value using options.
        /// </summary>
        public static void WriteValue(Utf8JsonWriter writer, object objectToWrite, JsonSerializerOptions options)
        {
            Guard.ThrowIfNull(writer);

            if (objectToWrite == null)
            {
                writer.WriteNullValue();
                return;
            }

            // Write known primitive types directly to avoid runtime type resolution (AOT-safe).
            switch (objectToWrite)
            {
                case string s:
                    writer.WriteStringValue(s);
                    break;
                case bool b:
                    writer.WriteBooleanValue(b);
                    break;
                case int i:
                    writer.WriteNumberValue(i);
                    break;
                case long l:
                    writer.WriteNumberValue(l);
                    break;
                case double d:
                    writer.WriteNumberValue(d);
                    break;
                case float f:
                    writer.WriteNumberValue(f);
                    break;
                case decimal dec:
                    writer.WriteNumberValue(dec);
                    break;
                case short sh:
                    writer.WriteNumberValue(sh);
                    break;
                case ushort us:
                    writer.WriteNumberValue(us);
                    break;
                case uint ui:
                    writer.WriteNumberValue(ui);
                    break;
                case ulong ul:
                    writer.WriteNumberValue(ul);
                    break;
                case byte by:
                    writer.WriteNumberValue(by);
                    break;
                case sbyte sb:
                    writer.WriteNumberValue(sb);
                    break;
                case DateTime dt:
                    writer.WriteStringValue(dt);
                    break;
                case DateTimeOffset dto:
                    writer.WriteStringValue(dto);
                    break;
                case DateOnly dateOnly:
                    writer.WriteStringValue(dateOnly.ToString("O"));
                    break;
                case TimeSpan ts:
                    writer.WriteStringValue(ts.ToString());
                    break;
                case Guid g:
                    writer.WriteStringValue(g);
                    break;
                case byte[] ba:
                    writer.WriteBase64StringValue(ba);
                    break;
                case char c:
                    writer.WriteStringValue(c.ToString());
                    break;
                default:
                    // Fallback for any other type - use JsonSerializer with the runtime type.
                    // This path should rarely be hit for Dotmim.Sync's known type set.
                    JsonSerializer.Serialize(writer, objectToWrite, objectToWrite.GetType(), options);
                    break;
            }
        }

        /// <summary>
        /// Read the next value and infer the type between string, bool, long, double or datetimeoffset.
        /// </summary>
        public override object Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => ReadValue(ref reader);

        /// <summary>
        /// Write value using options.
        /// </summary>
        public override void Write(
            Utf8JsonWriter writer,
            object value,
            JsonSerializerOptions options) => WriteValue(writer, value, options);
    }
}
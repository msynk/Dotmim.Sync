using System;
using System.Data;
using System.Globalization;

namespace Dotmim.Sync
{
    /// <summary>
    /// Sync Type Converter: Convert a value to another type.
    /// </summary>
    public static class SyncTypeConverter
    {
        /// <summary>
        /// Try to convert a value to another type.
        /// </summary>
        public static T TryConvertTo<T>(object value, CultureInfo provider = default)
        {
            if (value == null)
                return default;

            provider ??= CultureInfo.InvariantCulture;

            var typeOfT = typeof(T);
            var typeOfU = value.GetType();

            if (typeOfT == typeOfU)
                return (T)Convert.ChangeType(value, typeOfT, provider);

            if (typeOfT == typeof(short))
            {
                return (T)(object)Convert.ToInt16(value, provider);
            }
            else if (typeOfT == typeof(int))
            {
                return (T)(object)Convert.ToInt32(value, provider);
            }
            else if (typeOfT == typeof(long))
            {
                return (T)(object)Convert.ToInt64(value, provider);
            }
            else if (typeOfT == typeof(ushort))
            {
                return (T)(object)Convert.ToUInt16(value, provider);
            }
            else if (typeOfT == typeof(uint))
            {
                return (T)(object)Convert.ToUInt32(value, provider);
            }
            else if (typeOfT == typeof(ulong))
            {
                return (T)(object)Convert.ToUInt64(value, provider);
            }
            else if (typeOfT == typeof(DateOnly))
            {
                if (value is DateTimeOffset dateTimeOffset)
                    return (T)(object)DateOnly.FromDateTime(dateTimeOffset.DateTime);

                string valueStr = value.ToString();
                if (DateOnly.TryParse(valueStr, provider, DateTimeStyles.None, out DateOnly dateOnly))
                    return (T)(object)dateOnly;
                else if (typeOfU == typeof(long))
                    return (T)(object)DateOnly.FromDateTime(new DateTime((long)value));
                else
                    return (T)(object)DateOnly.FromDateTime(Convert.ToDateTime(value, provider));
            }
            else if (typeOfT == typeof(DateTime))
            {
                if (value is DateTimeOffset dateTimeOffset)
                    return (T)(object)dateTimeOffset.DateTime;

                string valueStr = value.ToString();
                if (DateTime.TryParse(valueStr, provider, DateTimeStyles.None, out DateTime dateTime))
                    return (T)(object)dateTime;
                else if (typeOfU == typeof(long))
                    return (T)(object)new DateTime((long)value);
                else
                    return (T)(object)Convert.ToDateTime(value, provider);
            }
            else if (typeOfT == typeof(DateTimeOffset))
            {
                if (value is DateTime dateTime)
                    return (T)(object)new DateTimeOffset(dateTime);
                else if (DateTimeOffset.TryParse(value.ToString(), provider, DateTimeStyles.None, out DateTimeOffset dateTimeOffset))
                    return (T)(object)dateTimeOffset;
                else if (typeOfU == typeof(long))
                    return (T)(object)new DateTimeOffset(new DateTime((long)value));
                else
                    return (T)(object)new DateTimeOffset(Convert.ToDateTime(value, provider));
            }
            else if (typeOfT == typeof(string))
            {
                return (T)(object)value.ToString();
            }
            else if (typeOfT == typeof(byte))
            {
                return (T)(object)Convert.ToByte(value, provider);
            }
            else if (typeOfT == typeof(bool))
            {
                if (bool.TryParse(value.ToString(), out bool v))
                    return (T)(object)v;
                else if (value.ToString().Trim() == "0")
                    return (T)(object)false;
                else if (value.ToString().Trim() == "1")
                    return (T)(object)true;
                else
                    return (T)(object)Convert.ToBoolean(value, provider);
            }
            else if (typeOfT == typeof(Guid))
            {
                string valueStr = value.ToString();
                if (Guid.TryParse(valueStr, out Guid j))
                    return (T)(object)j;
                else if (value.GetType() == typeof(byte[]))
                    return (T)(object)new Guid((byte[])value);
                else
                    return (T)(object)new Guid(value.ToString());
            }
            else if (typeOfT == typeof(char))
            {
                return (T)(object)Convert.ToChar(value, provider);
            }
            else if (typeOfT == typeof(decimal))
            {
                return (T)(object)Convert.ToDecimal(value, provider);
            }
            else if (typeOfT == typeof(double))
            {
                return (T)(object)Convert.ToDouble(value, provider);
            }
            else if (typeOfT == typeof(float))
            {
                return (T)(object)Convert.ToSingle(value, provider);
            }
            else if (typeOfT == typeof(sbyte))
            {
                return (T)(object)Convert.ToSByte(value, provider);
            }
            else if (typeOfT == typeof(TimeSpan))
            {
                if (typeOfU == typeof(short) || typeOfU == typeof(int) || typeOfU == typeof(long)
                   || typeOfU == typeof(ushort) || typeOfU == typeof(uint) || typeOfU == typeof(ulong))
                    return (T)(object)TimeSpan.FromTicks(Convert.ToInt64(value, provider));
                if (TimeSpan.TryParse(value.ToString(), provider, out TimeSpan q))
                    return (T)(object)q;
            }
            else if (typeOfT == typeof(byte[]))
            {
                if (typeOfU == typeof(string))
                    return (T)(object)Convert.FromBase64String((string)value);
                else
                    return (T)(object)ConvertToByteArray(value);
            }
            else
            {
                throw new FormatTypeException(typeOfT);
            }

            return default;
        }

        /// <summary>
        /// Try to convert a value to another type.
        /// </summary>
        public static object TryConvertTo(object value, Type typeOfT, CultureInfo provider = default)
        {
            if (typeOfT == typeof(short))
                return TryConvertTo<short>(value, provider);
            else if (typeOfT == typeof(int))
                return TryConvertTo<int>(value, provider);
            else if (typeOfT == typeof(long))
                return TryConvertTo<long>(value, provider);
            else if (typeOfT == typeof(ushort))
                return TryConvertTo<ushort>(value, provider);
            else if (typeOfT == typeof(uint))
                return TryConvertTo<uint>(value, provider);
            else if (typeOfT == typeof(ulong))
                return TryConvertTo<ulong>(value, provider);
            else if (typeOfT == typeof(DateTime))
                return TryConvertTo<DateTime>(value, provider);
            else if (typeOfT == typeof(DateOnly))
                return TryConvertTo<DateOnly>(value, provider);
            else if (typeOfT == typeof(DateTimeOffset))
                return TryConvertTo<DateTimeOffset>(value, provider);
            else if (typeOfT == typeof(string))
                return TryConvertTo<string>(value, provider);
            else if (typeOfT == typeof(byte))
                return TryConvertTo<byte>(value, provider);
            else if (typeOfT == typeof(bool))
                return TryConvertTo<bool>(value, provider);
            else if (typeOfT == typeof(Guid))
                return TryConvertTo<Guid>(value, provider);
            else if (typeOfT == typeof(char))
                return TryConvertTo<char>(value, provider);
            else if (typeOfT == typeof(decimal))
                return TryConvertTo<decimal>(value, provider);
            else if (typeOfT == typeof(double))
                return TryConvertTo<double>(value, provider);
            else if (typeOfT == typeof(float))
                return TryConvertTo<float>(value, provider);
            else if (typeOfT == typeof(sbyte))
                return TryConvertTo<sbyte>(value, provider);
            else if (typeOfT == typeof(TimeSpan))
                return TryConvertTo<TimeSpan>(value, provider);
            else if (typeOfT == typeof(byte[]))
                return TryConvertTo<byte[]>(value, provider);
            else if (typeOfT == typeof(object))
                return value;
            else
                throw new FormatTypeException(typeOfT);
        }

        /// <summary>
        /// Convert a numeric value to its byte array representation without using dynamic dispatch.
        /// </summary>
        private static byte[] ConvertToByteArray(object value) => value switch
        {
            short s => BitConverter.GetBytes(s),
            int i => BitConverter.GetBytes(i),
            long l => BitConverter.GetBytes(l),
            ushort us => BitConverter.GetBytes(us),
            uint ui => BitConverter.GetBytes(ui),
            ulong ul => BitConverter.GetBytes(ul),
            float f => BitConverter.GetBytes(f),
            double d => BitConverter.GetBytes(d),
            bool b => BitConverter.GetBytes(b),
            char c => BitConverter.GetBytes(c),
            byte[] ba => ba,
            _ => throw new FormatTypeException(typeof(byte[])),
        };

        /// <summary>
        /// Try to convert a value from DbType to another type.
        /// </summary>
        public static object TryConvertFromDbType(object value, DbType typeOfT, CultureInfo provider = default)
        {
            if (typeOfT == DbType.AnsiString || typeOfT == DbType.String
                || typeOfT == DbType.StringFixedLength || typeOfT == DbType.AnsiStringFixedLength
                || typeOfT == DbType.Xml)
                return TryConvertTo<string>(value, provider);
            else if (typeOfT == DbType.Binary)
                return TryConvertTo<byte[]>(value, provider);
            else if (typeOfT == DbType.Boolean)
                return TryConvertTo<bool>(value, provider);
            else if (typeOfT == DbType.Byte)
                return TryConvertTo<byte>(value, provider);
            else if (typeOfT == DbType.Currency || typeOfT == DbType.Decimal)
                return TryConvertTo<decimal>(value, provider);
            else if (typeOfT == DbType.Date)
                return TryConvertTo<DateOnly>(value, provider);
            else if (typeOfT == DbType.DateTime || typeOfT == DbType.DateTime2)
                return TryConvertTo<DateTime>(value, provider);
            else if (typeOfT == DbType.DateTimeOffset)
                return TryConvertTo<DateTimeOffset>(value, provider);
            else if (typeOfT == DbType.Double)
                return TryConvertTo<double>(value, provider);
            else if (typeOfT == DbType.Guid)
                return TryConvertTo<Guid>(value, provider);
            else if (typeOfT == DbType.Int16)
                return TryConvertTo<short>(value, provider);
            else if (typeOfT == DbType.Int32)
                return TryConvertTo<int>(value, provider);
            else if (typeOfT == DbType.Int64)
                return TryConvertTo<long>(value, provider);
            else if (typeOfT == DbType.SByte)
                return TryConvertTo<sbyte>(value, provider);
            else if (typeOfT == DbType.Single)
                return TryConvertTo<float>(value, provider);
            else if (typeOfT == DbType.Time)
                return TryConvertTo<TimeSpan>(value, provider);
            else if (typeOfT == DbType.UInt16)
                return TryConvertTo<ushort>(value, provider);
            else if (typeOfT == DbType.UInt32)
                return TryConvertTo<uint>(value, provider);
            else if (typeOfT == DbType.UInt64)
                return TryConvertTo<ulong>(value, provider);
            else if (typeOfT == DbType.VarNumeric)
                return TryConvertTo<float>(value, provider);
            else if (typeOfT == DbType.Object)
                return TryConvertTo<byte[]>(value, provider);
            else
                throw new FormatDbTypeException(typeOfT);
        }
    }
}

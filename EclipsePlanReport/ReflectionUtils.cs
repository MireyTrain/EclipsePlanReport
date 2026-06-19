using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EclipsePlanReport
{
    /// <summary>
    /// Versionssichere Zugriffe auf ESAPI-Eigenschaften per Reflection.
    /// Nicht verfuegbare Eigenschaften liefern leere Strings statt Fehler -
    /// so bleiben im Bericht nur sicher verfuegbare Daten stehen.
    /// </summary>
    internal static class ReflectionUtils
    {
        public static readonly CultureInfo Num = CultureInfo.InvariantCulture;

        public static object GetPropertyValue(object obj, string propertyName)
        {
            if (obj == null || string.IsNullOrEmpty(propertyName))
                return null;
            try
            {
                var property = obj.GetType().GetProperty(propertyName);
                if (property == null)
                    return null;
                return property.GetValue(obj, null);
            }
            catch
            {
                return null;
            }
        }

        public static string GetStringProperty(object obj, string propertyName)
        {
            object value = GetPropertyValue(obj, propertyName);
            if (value == null)
                return "";
            if (value is DateTime)
                return ((DateTime)value).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            DateTime dateTime;
            if (propertyName.ToLowerInvariant().Contains("date") && DateTime.TryParse(value.ToString(), out dateTime))
                return dateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            return value.ToString();
        }

        public static string GetNestedStringProperty(object obj, string propertyName, string nestedPropertyName)
        {
            return GetStringProperty(GetPropertyValue(obj, propertyName), nestedPropertyName);
        }

        public static bool GetBoolProperty(object obj, string propertyName)
        {
            object value = GetPropertyValue(obj, propertyName);
            return value is bool && (bool)value;
        }

        public static IEnumerable<object> GetEnumerableProperty(object obj, string propertyName)
        {
            object value = GetPropertyValue(obj, propertyName);
            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null)
                return Enumerable.Empty<object>();
            return enumerable.Cast<object>();
        }

        /// <summary>Verbindet die Id-Eigenschaften aller Elemente einer ESAPI-Collection (z.B. Wedges, Boluses).</summary>
        public static string JoinEnumerableIds(object obj, string propertyName)
        {
            var ids = GetEnumerableProperty(obj, propertyName)
                .Select(item => FirstNonEmpty(GetStringProperty(item, "Id"), GetStringProperty(item, "Name")))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            return string.Join(", ", ids);
        }

        public static object GetFirstEnumerableItem(object value)
        {
            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null)
                return null;
            foreach (object item in enumerable)
                return item;
            return null;
        }

        public static object GetLastEnumerableItem(object value)
        {
            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null)
                return null;
            object last = null;
            foreach (object item in enumerable)
                last = item;
            return last;
        }

        public static double? GetNumericMember(object obj, string memberName)
        {
            if (obj == null || string.IsNullOrEmpty(memberName))
                return null;
            try
            {
                var property = obj.GetType().GetProperty(memberName);
                if (property != null)
                    return TryConvertDouble(property.GetValue(obj, null));
                var field = obj.GetType().GetField(memberName);
                if (field != null)
                    return TryConvertDouble(field.GetValue(obj));
            }
            catch
            {
            }
            return null;
        }

        public static double? TryConvertDouble(object value)
        {
            if (value == null)
                return null;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                double parsed;
                if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
                if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.GetCultureInfo("de-DE"), out parsed))
                    return parsed;
            }
            return null;
        }

        public static string FormatNumber(object value, string format)
        {
            double? number = TryConvertDouble(value);
            return number.HasValue ? number.Value.ToString(format, Num) : "";
        }

        public static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return "";
        }
    }
}

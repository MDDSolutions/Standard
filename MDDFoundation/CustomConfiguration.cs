using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace MDDFoundation
{
    //using MDDFoundation;
    //public class MyCustomConfiguration : CustomConfiguration
    //{
    //    public override void ApplyDefaults()
    //    {
    //        //Optional method to specify default values on first use or if file gets corrupted
    //        MyCustomSetting1 = default;
    //        MyCustomSetting2 = default;
    //    }
    //    public int MyCustomSetting1 { get; set; }
    //    public string MyCustomSetting2 { get; set; }
    //}

    //public static class GlobalStuff
    //{
    //    //some static property somewhere in the app
    //    //passing the file name here sets the property for saving as well
    //    public static MyCustomConfiguration Configuration = CustomConfiguration.Load<MyCustomConfiguration>("MyCustomConfiguration.xml");
    //    public static void SaveConfig()
    //    {
    //        Configuration.MyCustomSetting1 = 5;
    //        Configuration.Save();
    //    }
    //}

    public enum CustomConfigurationFormat
    {
        Auto,
        Xml,
        Json
    }

    public enum CustomConfigurationSavePolicy
    {
        Never,
        CreateIfMissing,
        CreateIfMissingOrInvalid,
        SeedMissingProperties,
        Always
    }

    public enum CustomConfigurationAppLayout
    {
        Normal,
        LauncherVersionDirectory
    }

    public sealed class CustomConfigurationLoadOptions
    {
        public CustomConfigurationFormat Format { get; set; } = CustomConfigurationFormat.Auto;
        public CustomConfigurationSavePolicy SavePolicy { get; set; } = CustomConfigurationSavePolicy.CreateIfMissing;
        public string ConfigDirectoryName { get; set; } = "config";
        public bool BackupInvalidFile { get; set; } = true;
        public JsonSerializerOptions JsonSerializerOptions { get; set; } = CustomConfiguration.CreateDefaultJsonOptions();
    }

    public sealed class CustomConfigurationPathInfo
    {
        internal CustomConfigurationPathInfo(string requestedFileName, string fullFileName, string appBaseDirectory,
            string appRootDirectory, string configDirectory, CustomConfigurationAppLayout appLayout,
            CustomConfigurationFormat format)
        {
            RequestedFileName = requestedFileName;
            FullFileName = fullFileName;
            AppBaseDirectory = appBaseDirectory;
            AppRootDirectory = appRootDirectory;
            ConfigDirectory = configDirectory;
            AppLayout = appLayout;
            Format = format;
        }

        public string RequestedFileName { get; }
        public string FullFileName { get; }
        public string AppBaseDirectory { get; }
        public string AppRootDirectory { get; }
        public string ConfigDirectory { get; }
        public CustomConfigurationAppLayout AppLayout { get; }
        public CustomConfigurationFormat Format { get; }
        public bool IsLauncherManaged => AppLayout == CustomConfigurationAppLayout.LauncherVersionDirectory;
    }

    public class CustomConfiguration
    {
        const string defaultfilename = "ConfigurationSettings.xml";
        static readonly JsonSerializerOptions DefaultJsonOptions = CreateDefaultJsonOptions();

        public void Save()
        {
            if (string.IsNullOrWhiteSpace(ResolvedFileName))
            {
                var pathInfo = ResolvePath(FileName);
                ApplyPathInfo(pathInfo);
            }

            SaveToFile(ResolvedFileName, ResolvedFormat, this, DefaultJsonOptions);
        }

        public static T Load<T>(string filename = null, bool withsave = false) where T : CustomConfiguration, new()
        {
            return Load<T>(filename, new CustomConfigurationLoadOptions
            {
                SavePolicy = withsave
                    ? CustomConfigurationSavePolicy.Always
                    : CustomConfigurationSavePolicy.CreateIfMissingOrInvalid
            });
        }

        public static T Load<T>(CustomConfigurationLoadOptions options) where T : CustomConfiguration, new()
        {
            return Load<T>(null, options);
        }

        public static T Load<T>(string filename, CustomConfigurationLoadOptions options) where T : CustomConfiguration, new()
        {
            options = options ?? new CustomConfigurationLoadOptions();
            var pathInfo = ResolvePath(filename, options);
            var fi = new FileInfo(pathInfo.FullFileName);
            T result = null;
            var shouldSave = false;

            if (fi.Exists)
            {
                try
                {
                    result = LoadExisting<T>(fi.FullName, pathInfo.Format, options, out var missingProperties);
                    shouldSave = options.SavePolicy == CustomConfigurationSavePolicy.Always
                        || (options.SavePolicy == CustomConfigurationSavePolicy.SeedMissingProperties && missingProperties);
                }
                catch (Exception ex) when (IsConfigurationParseException(ex))
                {
                    if (options.SavePolicy == CustomConfigurationSavePolicy.Never
                        || options.SavePolicy == CustomConfigurationSavePolicy.CreateIfMissing)
                    {
                        throw;
                    }

                    BackupInvalidConfiguration(fi, options);
                    result = CreateDefault<T>();
                    shouldSave = options.SavePolicy != CustomConfigurationSavePolicy.Never;
                }
            }
            else
            {
                result = CreateDefault<T>();
                shouldSave = options.SavePolicy != CustomConfigurationSavePolicy.Never;
            }

            result.ApplyPathInfo(pathInfo);

            if (shouldSave)
            {
                result.Save();
            }

            return result;
        }

        public virtual void ApplyDefaults()
        {
            // This method can be overridden in derived classes to apply default values
            // when the configuration is loaded for the first time or if the file gets corrupted.
        }

        public static List<T> FindConfigurations<T>(string path = null) where T : CustomConfiguration, new()
        {
            var result = new List<T>();
            if (string.IsNullOrWhiteSpace(path))
            {
                path = ResolveConfigDirectory(new CustomConfigurationLoadOptions());
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            foreach (var file in Directory.GetFiles(path, "*.*")
                .Where(f => IsKnownConfigExtension(Path.GetExtension(f))))
            {
                try
                {
                    var format = FormatFromFileName(file);
                    var loaded = DeserializeFile<T>(file, format, DefaultJsonOptions);
                    loaded.ApplyPathInfo(ResolvePath(file));
                    result.Add(loaded);
                }
                catch (Exception ex) when (IsConfigurationParseException(ex))
                {
                }
            }

            return result.Count == 0 ? null : result;
        }

        [XmlIgnore]
        [JsonIgnore]
        public string FileName { get; set; }

        [XmlIgnore]
        [JsonIgnore]
        public string ResolvedFileName { get; private set; }

        [XmlIgnore]
        [JsonIgnore]
        public string AppBaseDirectory { get; private set; }

        [XmlIgnore]
        [JsonIgnore]
        public string AppRootDirectory { get; private set; }

        [XmlIgnore]
        [JsonIgnore]
        public string ConfigDirectory { get; private set; }

        [XmlIgnore]
        [JsonIgnore]
        public CustomConfigurationFormat ResolvedFormat { get; private set; } = CustomConfigurationFormat.Xml;

        [XmlIgnore]
        [JsonIgnore]
        public CustomConfigurationAppLayout AppLayout { get; private set; } = CustomConfigurationAppLayout.Normal;

        [XmlIgnore]
        [JsonIgnore]
        public bool IsLauncherManaged => AppLayout == CustomConfigurationAppLayout.LauncherVersionDirectory;

        public static string FullFileName(string filename)
        {
            return ResolvePath(filename).FullFileName;
        }

        public static string ResolveConfigDirectory(CustomConfigurationLoadOptions options = null)
        {
            options = options ?? new CustomConfigurationLoadOptions();
            return ResolvePath(defaultfilename, options).ConfigDirectory;
        }

        public static CustomConfigurationPathInfo ResolvePath(string filename, CustomConfigurationLoadOptions options = null)
        {
            options = options ?? new CustomConfigurationLoadOptions();
            if (string.IsNullOrWhiteSpace(filename))
            {
                filename = defaultfilename;
            }

            var appPaths = FoundationAppPaths.Current;
            var appBaseDirectory = appPaths.AppBaseDirectory;
            var appRootDirectory = appPaths.AppRootDirectory;
            var configDirectory = Path.Combine(appRootDirectory, options.ConfigDirectoryName);
            var format = ResolveFormat(filename, options.Format);
            var fullFileName = Path.IsPathRooted(filename)
                ? Path.GetFullPath(filename)
                : Path.GetFullPath(Path.Combine(configDirectory, filename));

            return new CustomConfigurationPathInfo(filename, fullFileName, appBaseDirectory, appRootDirectory,
                configDirectory, appPaths.IsLauncherManaged
                    ? CustomConfigurationAppLayout.LauncherVersionDirectory
                    : CustomConfigurationAppLayout.Normal, format);
        }

        internal static JsonSerializerOptions CreateDefaultJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };
        }

        static T LoadExisting<T>(string fileName, CustomConfigurationFormat format, CustomConfigurationLoadOptions options,
            out bool missingProperties) where T : CustomConfiguration, new()
        {
            var loaded = DeserializeFile<T>(fileName, format, options.JsonSerializerOptions);
            var existingNames = ReadTopLevelPropertyNameSet(fileName, format);
            missingProperties = options.SavePolicy == CustomConfigurationSavePolicy.SeedMissingProperties
                && HasMissingTopLevelProperties<T>(existingNames, format);

            if (!missingProperties)
            {
                return loaded;
            }

            var seeded = CreateDefault<T>();
            foreach (var property in GetSerializableProperties(typeof(T), format))
            {
                if (!existingNames.Contains(GetSerializedName(property, format)))
                {
                    continue;
                }

                var value = property.GetValue(loaded);
                property.SetValue(seeded, value);
            }

            return seeded;
        }

        static T CreateDefault<T>() where T : CustomConfiguration, new()
        {
            var result = new T();
            result.ApplyDefaults();
            return result;
        }

        static T DeserializeFile<T>(string fileName, CustomConfigurationFormat format, JsonSerializerOptions jsonOptions)
            where T : CustomConfiguration, new()
        {
            switch (format)
            {
                case CustomConfigurationFormat.Xml:
                    using (var stream = File.OpenRead(fileName))
                    {
                        var ser = new XmlSerializer(typeof(T));
                        return (T)ser.Deserialize(stream);
                    }

                case CustomConfigurationFormat.Json:
                    var json = File.ReadAllText(fileName);
                    return JsonSerializer.Deserialize<T>(json, jsonOptions) ?? CreateDefault<T>();

                case CustomConfigurationFormat.Auto:
                    return TryDeserializeBoth<T>(fileName, jsonOptions);

                default:
                    throw new NotSupportedException($"Unsupported configuration format: {format}");
            }
        }

        static T TryDeserializeBoth<T>(string fileName, JsonSerializerOptions jsonOptions)
            where T : CustomConfiguration, new()
        {
            Exception firstFailure;
            try
            {
                return DeserializeFile<T>(fileName, CustomConfigurationFormat.Json, jsonOptions);
            }
            catch (Exception ex) when (IsConfigurationParseException(ex))
            {
                firstFailure = ex;
            }

            try
            {
                return DeserializeFile<T>(fileName, CustomConfigurationFormat.Xml, jsonOptions);
            }
            catch (Exception ex) when (IsConfigurationParseException(ex))
            {
                throw new InvalidOperationException("Configuration could not be read as JSON or XML.", firstFailure);
            }
        }

        static void SaveToFile(string fileName, CustomConfigurationFormat format, object value,
            JsonSerializerOptions jsonOptions)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fileName) ?? ".");
            switch (format)
            {
                case CustomConfigurationFormat.Xml:
                    using (var stream = File.Create(fileName))
                    {
                        var ser = new XmlSerializer(value.GetType());
                        ser.Serialize(stream, value);
                    }
                    break;

                case CustomConfigurationFormat.Json:
                case CustomConfigurationFormat.Auto:
                    var json = JsonSerializer.Serialize(value, value.GetType(), jsonOptions);
                    File.WriteAllText(fileName, json);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported configuration format: {format}");
            }
        }

        static void BackupInvalidConfiguration(FileInfo fi, CustomConfigurationLoadOptions options)
        {
            if (!options.BackupInvalidFile || !fi.Exists)
            {
                return;
            }

            var backupName = fi.FullName + ".invalid." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            fi.MoveTo(backupName);
        }

        static bool HasMissingTopLevelProperties<T>(HashSet<string> existingNames, CustomConfigurationFormat format)
        {
            return GetSerializableProperties(typeof(T), format)
                .Select(p => GetSerializedName(p, format))
                .Any(name => !existingNames.Contains(name));
        }

        static HashSet<string> ReadTopLevelPropertyNameSet(string fileName, CustomConfigurationFormat format)
        {
            var comparison = format == CustomConfigurationFormat.Json || format == CustomConfigurationFormat.Auto
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            return new HashSet<string>(ReadTopLevelPropertyNames(fileName, format), comparison);
        }

        static IEnumerable<string> ReadTopLevelPropertyNames(string fileName, CustomConfigurationFormat format)
        {
            switch (format)
            {
                case CustomConfigurationFormat.Xml:
                    var xmlDocument = XDocument.Load(fileName);
                    return xmlDocument.Root == null
                        ? Enumerable.Empty<string>()
                        : xmlDocument.Root.Elements().Select(e => e.Name.LocalName)
                            .Concat(xmlDocument.Root.Attributes().Select(a => a.Name.LocalName))
                            .ToList();

                case CustomConfigurationFormat.Json:
                    using (var jsonDocument = JsonDocument.Parse(File.ReadAllText(fileName)))
                    {
                        return jsonDocument.RootElement.ValueKind == JsonValueKind.Object
                            ? jsonDocument.RootElement.EnumerateObject().Select(p => p.Name).ToList()
                            : Enumerable.Empty<string>();
                    }

                case CustomConfigurationFormat.Auto:
                    try
                    {
                        return ReadTopLevelPropertyNames(fileName, CustomConfigurationFormat.Json);
                    }
                    catch (Exception ex) when (IsConfigurationParseException(ex))
                    {
                        return ReadTopLevelPropertyNames(fileName, CustomConfigurationFormat.Xml);
                    }

                default:
                    throw new NotSupportedException($"Unsupported configuration format: {format}");
            }
        }

        static IEnumerable<PropertyInfo> GetSerializableProperties(Type type, CustomConfigurationFormat format)
        {
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<XmlIgnoreAttribute>() == null)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null);
        }

        static string GetSerializedName(PropertyInfo property, CustomConfigurationFormat format)
        {
            if (format == CustomConfigurationFormat.Json || format == CustomConfigurationFormat.Auto)
            {
                return property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            }

            var xmlElement = property.GetCustomAttribute<XmlElementAttribute>();
            if (!string.IsNullOrWhiteSpace(xmlElement?.ElementName))
            {
                return xmlElement.ElementName;
            }

            var xmlAttribute = property.GetCustomAttribute<XmlAttributeAttribute>();
            if (!string.IsNullOrWhiteSpace(xmlAttribute?.AttributeName))
            {
                return xmlAttribute.AttributeName;
            }

            return property.Name;
        }

        static CustomConfigurationFormat ResolveFormat(string filename, CustomConfigurationFormat requestedFormat)
        {
            if (requestedFormat != CustomConfigurationFormat.Auto)
            {
                return requestedFormat;
            }

            return FormatFromFileName(filename);
        }

        static CustomConfigurationFormat FormatFromFileName(string filename)
        {
            var extension = Path.GetExtension(filename);
            if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
            {
                return CustomConfigurationFormat.Xml;
            }

            if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
            {
                return CustomConfigurationFormat.Json;
            }

            return CustomConfigurationFormat.Auto;
        }

        static bool IsKnownConfigExtension(string extension)
        {
            return string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsConfigurationParseException(Exception ex)
        {
            return ex is InvalidOperationException
                || ex is JsonException
                || ex is System.Xml.XmlException
                || ex is IOException;
        }

        void ApplyPathInfo(CustomConfigurationPathInfo pathInfo)
        {
            FileName = pathInfo.RequestedFileName;
            ResolvedFileName = pathInfo.FullFileName;
            AppBaseDirectory = pathInfo.AppBaseDirectory;
            AppRootDirectory = pathInfo.AppRootDirectory;
            ConfigDirectory = pathInfo.ConfigDirectory;
            ResolvedFormat = pathInfo.Format == CustomConfigurationFormat.Auto
                ? FormatFromFileName(pathInfo.FullFileName)
                : pathInfo.Format;
            AppLayout = pathInfo.AppLayout;
        }
    }
}

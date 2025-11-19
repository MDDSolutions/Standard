using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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


    public class CustomConfiguration
    {
        const string defaultfilename = "ConfigurationSettings.xml";
        public void Save()
        {
            using (Stream stream = File.Create(FullFileName(FileName)))
            {
                XmlSerializer ser = new XmlSerializer(this.GetType());
                ser.Serialize(stream, this);
            }
        }
        public static T Load<T>(string filename = null, bool withsave = false) where T : CustomConfiguration, new()
        {
            var fi = new FileInfo(FullFileName(filename));
            T r = null;

            if (fi.Exists)
            {
                using (Stream stream = fi.OpenRead())
                {
                    try
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(T));
                        r = (T)ser.Deserialize(stream);
                    }
                    catch (InvalidOperationException)
                    {
                        stream.Close();
                        fi.Delete();
                    }
                }
            }

            if (r == null)
            {
                r = new T();
                r.ApplyDefaults();
            }
            if (r.FileName != filename && filename != defaultfilename)
            {
                r.FileName = filename;
                r.Save();
                withsave = false;
            }
            if (withsave || !fi.Exists) r.Save();
            return r;
        }
        public virtual void ApplyDefaults()
        {
            // This method can be overridden in derived classes to apply default values
            // when the configuration is loaded for the first time or if the file is corrupted.
        }
        public static List<T> FindConfigurations<T>(string path = null) where T : CustomConfiguration, new()
        {
            List<T> result = null;
            if (string.IsNullOrEmpty(path))
            {
                var fi = new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location);
                path = fi.DirectoryName ?? AppDomain.CurrentDomain.BaseDirectory;
            }
            foreach (var file in Directory.GetFiles(path, "*.xml"))
            {
                using (Stream stream = File.OpenRead(file))
                {
                    try
                    {
                        XmlSerializer ser = new XmlSerializer(typeof(T));
                        var r = (T)ser.Deserialize(stream);
                        if (result == null) result = new List<T>();
                        result.Add(r);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            return result;
        }
        public string FileName { get; set; }
        public static string FullFileName(string filename)
        {
            // If no filename provided, use your default
            if (string.IsNullOrWhiteSpace(filename))
            {
                filename = defaultfilename;
            }

            // If it's not rooted (relative or just a bare filename), resolve it next to the app
            if (!Path.IsPathRooted(filename))
            {
                var baseDir = AppContext.BaseDirectory; // works in single-file too
                return Path.Combine(baseDir, filename);
            }

            // Already an absolute path, just return it as-is
            return filename;
        }

        //public static string FullFileName(string filename)
        //{
        //    if (string.IsNullOrWhiteSpace(filename) || !filename.Contains(@"\"))
        //    {
        //        FileInfo ass = new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location);
        //        if (string.IsNullOrWhiteSpace(filename))
        //            filename = defaultfilename;
        //        var dir = ass.DirectoryName ?? AppDomain.CurrentDomain.BaseDirectory;
        //        return Path.Combine(dir, filename);
        //    }
        //    return filename;
        //}
    }
}

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
    //    public MyCustomConfiguration()
    //    {
    //        //Optional constructor to specify default values on first use or if file gets corrupted
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
        public static T Load<T>(string filename = null) where T : CustomConfiguration, new()
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

            if (r == null) r = new T();
            if (r.FileName != filename && filename != defaultfilename)
            {
                r.FileName = filename;
                r.Save();
            }
            if (!fi.Exists) r.Save();
            return r;
        }
        public string FileName { get; set; }
        public static string FullFileName(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename) || !filename.Contains(@"\"))
            {
                FileInfo ass = new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrWhiteSpace(filename))
                    filename = defaultfilename;
                return Path.Combine(ass.DirectoryName, filename);
            }
            return filename;
        }
    }
}

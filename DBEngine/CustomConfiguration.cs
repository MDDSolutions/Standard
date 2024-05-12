using System;
using System.IO;
using System.Xml.Serialization;

namespace MDDDataAccess
{

    //DEPRECATED - use MDDFoundation



    //using MDDDataAccess;
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


    //public class CustomConfiguration
    //{
    //    public void Save()
    //    {
    //        using (Stream stream = File.Create(FullFileName))
    //        {
    //            XmlSerializer ser = new XmlSerializer(this.GetType());
    //            ser.Serialize(stream, this);
    //        }
    //    }
    //    public static T Load<T>(string FileNameOnly = "ConfigurationSettings.xml") where T : new()
    //    {
    //        if (FileName != FileNameOnly)
    //            FileName = FileNameOnly;
    //        if (!File.Exists(FullFileName))
    //            return new T();
    //        using (Stream stream = File.OpenRead(FullFileName))
    //        {
    //            try
    //            {
    //                XmlSerializer ser = new XmlSerializer(typeof(T));
    //                return (T)ser.Deserialize(stream);
    //            }
    //            catch (InvalidOperationException)
    //            {
    //                stream.Close();
    //                File.Delete(FullFileName);
    //                return new T();
    //            }
    //        }
    //    }
    //    public static string FileName { get; set; }
    //    public static string FullFileName
    //    {
    //        get
    //        {
    //            FileInfo ass = new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location);
    //            if (string.IsNullOrWhiteSpace(FileName))
    //                FileName = "ConfigurationSettings.xml";
    //            return Path.Combine(ass.DirectoryName, FileName);
    //        }
    //    }
    //}
}

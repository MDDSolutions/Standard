using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBEngineUnitTests
{
    public class MoarEnums
    {
        public TestEnum TestEnum1 { get; set; }
        public TestEnum2 TestEnum2 { get; set; }
        public TestEnum TestEnum3 { get; set; }
    }
    public enum TestEnum
    {
        NoValue, Value1, Value2, Value3
    }
    public enum TestEnum2 : byte
    {
        NoValue, Value1, Value2, Value3
    }
    public class MoarChars
    {
        public char TestChar1 { get; set; }
        public char TestChar2 { get; set; }
        public char TestChar3 { get; set; }
        public char TestChar4 { get; set; }
    }
}

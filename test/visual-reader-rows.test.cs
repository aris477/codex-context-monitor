using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

public static class VisualReaderRowsTests {
    static int checks;
    static Type line=typeof(VisualReader).GetNestedType("TextLine",BindingFlags.NonPublic);
    static Type list=typeof(List<>).MakeGenericType(line);
    static MethodInfo distinct=typeof(VisualReader).GetMethod("DistinctPaintedRows",BindingFlags.Static|BindingFlags.NonPublic);
    static object Row(string text,double x,double y,double width=371,double height=42) {
        object row=Activator.CreateInstance(line,true);
        foreach(var value in new Dictionary<string,object>{{"text",text},{"x",x},{"y",y},{"width",width},{"height",height}})
            line.GetField(value.Key).SetValue(row,value.Value);
        return row;
    }
    static void Check(int expected,double dpi,params object[] rows) {
        var input=(IList)Activator.CreateInstance(list);
        foreach(var row in rows)input.Add(row);
        var result=(IList)distinct.Invoke(null,new object[]{input,dpi});
        checks++;if(result.Count!=expected)throw new Exception("Check "+checks+": expected "+expected+", got "+result.Count);
    }
    public static void Main() {
        Check(1,1.5,Row("chat",12,283),Row("chat",12,282));
        Check(1,1.5,Row("chat",12,283),Row("chat",13,284,372,43));
        Check(2,1.5,Row("chat",12,283),Row("chat",12,1078));
        Check(2,1.5,Row("chat",12,283),Row("other",12,283));
        Check(2,1.5,Row("chat",12,283),Row("chat",12,320));
        Check(2,1.5,Row("chat",12,283),Row("chat",12,289));
        Check(2,1.5,Row("chat",12,283),Row("chat",32,283));
        Check(2,1.5,Row("chat",12,283),Row("chat",12,283,250));
        Check(2,1.5,Row("chat",12,283),Row("chat",12,283,371,30));
        Check(2,1.5,Row("chat",12,283),Row("chat",12,282),Row("chat",12,1078));
        Check(0,1.5);
        Check(1,1,Row("chat",12,283),Row("chat",12,285));
        Check(2,1,Row("chat",12,283),Row("chat",12,286));
        Check(1,2,Row("chat",12,283),Row("chat",12,287));
        Console.WriteLine("PASS "+checks+" painted-row checks");
    }
}

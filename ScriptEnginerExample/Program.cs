namespace ScriptEnginerTester;

internal class Program
{
    static void Main(string[] args)
    {
        //Create a instance of the script engine and compile the scripts in the defined directroy (This will be any files with .cs file extension)
        var scriptEngine = new ScriptEngine.Engine("Scripts");
        
        //Execute script Tert.cs at Namespace: TestSpace with class TestClass function Test. EX: TestSpace.TestClass.Test() is normal equivalent with return type of void
        scriptEngine.ExecuteFunction("Test", "TestSpace","TestClass","Test");

        //Execute script Tert.cs at Namespace: TestSpace with class TestClass function Test2. EX: TestSpace.TestClass.Test2() is normal equivalent with return type of int
        var info = scriptEngine.ExecuteFunction<int>("Test", "TestSpace", "TestClass", "Test2");

        //Execute script Tert.cs at Namespace: TestSpace with class TestClass2 function Test. EX: TestSpace.TestClass2.Test() is normal equivalent with return type of void passing a parameter string
        scriptEngine.ExecuteFunction("Test2", "TestSpace", "TestClass2", "Test", "Program to script here!");
        
        Console.WriteLine($"Got {info} from the script engine! :)");
    }
}

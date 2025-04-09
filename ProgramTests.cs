using System;

namespace ImagingTool
{
    public static class ProgramTests
    {
        public static void RunAllTests()
        {
            Console.WriteLine("Running basic tests...");

            TestAddition();
            TestStringEquality();

            Console.WriteLine("All tests finished.");
        }

        private static void TestAddition()
        {
            int result = 2 + 2;
            if (result == 4)
            {
                Console.WriteLine("TestAddition passed.");
            }
            else
            {
                Console.WriteLine("TestAddition failed: Expected 4, got " + result);
            }
        }

        private static void TestStringEquality()
        {
            string expected = "hello";
            string actual = "he" + "llo";

            if (expected == actual)
            {
                Console.WriteLine("TestStringEquality passed.");
            }
            else
            {
                Console.WriteLine("TestStringEquality failed: Expected 'hello', got '" + actual + "'");
            }
        }
    }
}

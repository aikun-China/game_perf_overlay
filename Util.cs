using System;

namespace PerfMonitor
{
    // Helpers that are present in .NET Core / .NET 5+ but missing from .NET
    // Framework 4.8. Kept in one file so the rest of the codebase can stay
    // similar between target frameworks.
    internal static class Util
    {
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static byte ClampToByte(int value)
        {
            if (value <= 0) return 0;
            if (value >= 255) return 255;
            return (byte)value;
        }
    }
}

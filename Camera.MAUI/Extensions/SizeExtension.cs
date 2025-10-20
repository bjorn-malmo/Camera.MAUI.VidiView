using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if ANDROID
using Size = Android.Util.Size;
#else
using Size = Microsoft.Maui.Graphics.Size;
#endif

namespace Camera.MAUI;

internal static class SizeExtension
{
    public static Size ChooseMaximum(this IEnumerable<Size> choices)
    {
        return choices.OrderBy(x => x.Width * x.Height).Last();
    }

    public static Size ChooseClosestMatch(this IEnumerable<Size> choices, Size preferred)
    {
        Size bestChoice = choices.First();
        double bestDiff = double.MaxValue;

        foreach (Size size in choices)
        {
            // Calculate difference in aspect ratio and pixel count
            double aspectRatioDisplay = (double)preferred.Width / preferred.Height;
            double aspectRatioSize = (double)size.Width / size.Height;
            double aspectDiff = Math.Abs(aspectRatioDisplay - aspectRatioSize);

            int pixelDiff = (int) Math.Abs((size.Width * size.Height) - (preferred.Width * preferred.Height));

            // Combine aspect and pixel diff for best match
            double diff = aspectDiff * 1000 + pixelDiff;

            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestChoice = size;
            }
        }

        return bestChoice;
    }
}

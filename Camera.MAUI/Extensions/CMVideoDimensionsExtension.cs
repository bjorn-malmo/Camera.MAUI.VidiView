using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if IOS
namespace CoreMedia;

internal static class CMVideoDimensionsExtension
{
    public static CMVideoDimensions ChooseMaximum(this IEnumerable<CMVideoDimensions> choices)
    {
        return choices.OrderBy(x => x.Width * x.Height).Last();
    }

    public static CMVideoDimensions ChooseClosestMatch(this IEnumerable<CMVideoDimensions> choices, CMVideoDimensions preferred)
    {
        CMVideoDimensions bestChoice = choices.First();
        double bestDiff = double.MaxValue;

        foreach (CMVideoDimensions size in choices)
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

    public static CMVideoDimensions ToDimension(this Size size)
    {
        return new CMVideoDimensions
        {
            Width = (int)size.Width,
            Height = (int)size.Height
        };
    }
}
#endif

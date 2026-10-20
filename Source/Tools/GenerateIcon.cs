using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class GenerateIcon
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var images = new List<byte[]>();
        int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        foreach (int size in sizes)
        {
            using (var large = Draw(size * 4))
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var bytes = new MemoryStream())
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(large, new Rectangle(0, 0, size, size));
                bitmap.Save(bytes, ImageFormat.Png);
                images.Add(bytes.ToArray());
            }
        }
        using (var output = new BinaryWriter(File.Create(args[0])))
        {
            output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int index = 0; index < sizes.Length; index++)
            {
                output.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                output.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)32);
                output.Write(images[index].Length); output.Write(offset); offset += images[index].Length;
            }
            foreach (var bytes in images) output.Write(bytes);
        }
        return 0;
    }

    private static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(size / 256f, size / 256f);
            using (var shape = Rounded(new RectangleF(8, 8, 240, 240), 49))
            using (var background = new LinearGradientBrush(new Point(20, 10), new Point(235, 250), Color.FromArgb(16, 27, 48), Color.FromArgb(40, 40, 40)))
            using (var border = new Pen(Color.FromArgb(38, 255, 255, 255), 2))
            {
                graphics.FillPath(background, shape); graphics.DrawPath(border, shape);
            }
            using (var wrench = new GraphicsPath())
            using (var fill = new LinearGradientBrush(new Point(60, 200), new Point(193, 50), Color.FromArgb(151, 175, 195), Color.FromArgb(234, 242, 248)))
            {
                wrench.AddLines(new[] { new PointF(179, 47), new PointF(155, 71), new PointF(161, 96), new PointF(186, 102), new PointF(211, 77) });
                wrench.AddBezier(211,77,225,112,201,143,167,135);
                wrench.AddLine(167,135,106,196);
                wrench.AddBezier(106,196,90,215,63,211,52,193);
                wrench.AddBezier(52,193,41,175,46,155,63,146);
                wrench.AddLine(63,146,125,85);
                wrench.AddBezier(125,85,117,51,147,30,179,47);
                wrench.CloseFigure(); graphics.FillPath(fill, wrench);
            }
            using (var hole = new SolidBrush(Color.FromArgb(26, 36, 51))) graphics.FillEllipse(hole, 65, 166, 22, 22);
        }
        return bitmap;
    }
    private static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var result = new GraphicsPath(); float diameter = radius * 2;
        result.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        result.AddArc(rectangle.Right-diameter, rectangle.Top, diameter, diameter, 270, 90);
        result.AddArc(rectangle.Right-diameter, rectangle.Bottom-diameter, diameter, diameter, 0, 90);
        result.AddArc(rectangle.Left, rectangle.Bottom-diameter, diameter, diameter, 90, 90);
        result.CloseFigure(); return result;
    }
}

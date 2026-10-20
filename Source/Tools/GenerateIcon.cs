using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class GenerateIcon
{
    private static string subject;
    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2) return 2;
        subject = args.Length == 2 ? args[1] : "app";
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
            using (var shape = Rounded(new RectangleF(8, 8, 240, 240), subject == "app" ? 49 : 32))
            using (Brush background = subject == "app"
                ? (Brush)new LinearGradientBrush(new Point(20, 10), new Point(235, 250), Color.FromArgb(16, 27, 48), Color.FromArgb(40, 40, 40))
                : new SolidBrush(Color.FromArgb(32, 32, 32)))
            using (var border = new Pen(Color.FromArgb(38, 255, 255, 255), 2))
            {
                graphics.FillPath(background, shape); if (subject == "app") graphics.DrawPath(border, shape);
            }
            if (subject == "security")
            {
                using (var pen = new Pen(Color.FromArgb(242, 242, 242), 12) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var accent = new Pen(Color.FromArgb(96,205,255), 12) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var shield = new GraphicsPath())
                {
                    shield.AddLines(new[] { new PointF(128, 46), new PointF(191, 71), new PointF(186, 139) });
                    shield.AddBezier(186,139,180,169,158,191,128,208);
                    shield.AddBezier(128,208,98,191,76,169,70,139);
                    shield.AddLine(70,139,65,71); shield.CloseFigure();
                    graphics.DrawPath(pen, shield);
                    graphics.DrawLine(accent, 57, 204, 203, 54);
                }
                return bitmap;
            }
            if (subject == "taskbar")
            {
                using (var pen = new Pen(Color.FromArgb(242, 242, 242), 12) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var accent = new Pen(Color.FromArgb(96,205,255), 12))
                using (var fill = new SolidBrush(Color.FromArgb(242, 242, 242)))
                {
                    graphics.DrawRectangle(pen, 48, 59, 160, 126);
                    graphics.DrawLine(accent, 54, 146, 202, 146);
                    for (int i = 0; i < 3; i++) graphics.FillRectangle(fill, 98 + i*22, 158, 12, 12);
                    graphics.DrawLine(pen, 128, 188, 128, 208);
                    graphics.DrawLine(pen, 97, 208, 159, 208);
                }
                return bitmap;
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

param([string]$Source = (Join-Path $PSScriptRoot '../logo.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class LogoAssets
{
    public static void Build(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        using (var original = new Bitmap(source))
        // Source-specific region excludes the frame, caption and outer margins.
        using (var cutout = new Bitmap(600, 645, PixelFormat.Format32bppArgb))
        {
            int left=600, top=645, right=0, bottom=0;
            for (int y=0;y<645;y++) for(int x=0;x<600;x++)
            {
                int sx=x+490,sy=y+330;
                if(sx>1035&&(sy<747||sy>779))continue;
                if(sy>964)continue;
                if(sy>=900){double ex=(sx-769)/266.0,ey=(sy-898)/66.0;if(ex*ex+ey*ey>1.012)continue;}
                var c=original.GetPixel(x+490,y+330);
                double max=Math.Max(c.R,Math.Max(c.G,c.B));
                double min=Math.Min(c.R,Math.Min(c.G,c.B));
                double saturation=max==0?0:(max-min)/max;
                // Remove white paper and its faint tint, retaining the original colored mark.
                double coverage=Math.Max(0,Math.Min(1,(saturation-0.085)/0.065));
                int alpha=(int)Math.Round(255*coverage);
                if(alpha==0)continue;
                int r=(int)Math.Max(0,Math.Min(255,(c.R-255*(1-coverage))/coverage));
                int g=(int)Math.Max(0,Math.Min(255,(c.G-255*(1-coverage))/coverage));
                int b=(int)Math.Max(0,Math.Min(255,(c.B-255*(1-coverage))/coverage));
                cutout.SetPixel(x,y,Color.FromArgb(alpha,r,g,b));
                if(alpha>32){left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
            }
            if(right<=left||bottom<=top)throw new InvalidOperationException("Logo subject was not found.");
            using(var logo=new Bitmap(512,512,PixelFormat.Format32bppArgb))
            {
                using(var graphics=Graphics.FromImage(logo))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingQuality=CompositingQuality.HighQuality;
                    graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;
                    double scale=464.0/Math.Max(right-left+1,bottom-top+1);
                    float width=(float)((right-left+1)*scale),height=(float)((bottom-top+1)*scale);
                    graphics.DrawImage(cutout,new RectangleF((512-width)/2,(512-height)/2,width,height),new RectangleF(left,top,right-left+1,bottom-top+1),GraphicsUnit.Pixel);
                }
                logo.Save(Path.Combine(destination,"logo.png"),ImageFormat.Png);
                int[] sizes={16,24,32,48,64,128,256};
                var images=new byte[sizes.Length][];
                for(int i=0;i<sizes.Length;i++)using(var resized=new Bitmap(sizes[i],sizes[i],PixelFormat.Format32bppArgb))
                {
                    using(var graphics=Graphics.FromImage(resized)){graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;graphics.DrawImage(logo,0,0,sizes[i],sizes[i]);}
                    using(var buffer=new MemoryStream()){resized.Save(buffer,ImageFormat.Png);images[i]=buffer.ToArray();}
                }
                using(var writer=new BinaryWriter(File.Create(Path.Combine(destination,"logo.ico"))))
                {
                    writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);
                    int offset=6+16*sizes.Length;
                    for(int i=0;i<sizes.Length;i++)
                    {
                        writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));
                        writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);
                        writer.Write(images[i].Length);writer.Write(offset);offset+=images[i].Length;
                    }
                    foreach(var image in images)writer.Write(image);
                }
            }
        }
    }
}
'@
$assetDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/DbStudio/Assets'))
[LogoAssets]::Build([IO.Path]::GetFullPath($Source),$assetDirectory)
Write-Output "Logo assets written to $assetDirectory"

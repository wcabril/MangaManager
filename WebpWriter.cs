using System.IO;
using ImageMagick;

namespace MangaManager
{
    // Encoder WebP da saída da nuvem.
    //
    // A escolha do Magick.NET aqui é de licença, não de técnica: o ImageSharp 4
    // pede licença paga e imprime o aviso a cada build, e a linha 2.x, que era
    // Apache-2.0, foi descontinuada com vulnerabilidades altas em aberto. O
    // Magick.NET é Apache-2.0 e mantido, ao custo do binário nativo que carrega.
    internal static class WebpWriter
    {
        // Recebe o bitmap já redimensionado e grava WebP com perda.
        //
        // A ponte entre as duas bibliotecas é um BMP em memória: sem compressão,
        // custa quase nada, enquanto um PNG intermediário pagaria duas vezes pela
        // compactação só para jogá-la fora em seguida.
        public static void Save(System.Drawing.Bitmap bmp, string destPath, int quality)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            ms.Position = 0;

            using var image = new MagickImage(ms);
            image.Format  = MagickFormat.WebP;
            image.Quality = (uint)quality;
            image.Write(destPath);
        }
    }
}

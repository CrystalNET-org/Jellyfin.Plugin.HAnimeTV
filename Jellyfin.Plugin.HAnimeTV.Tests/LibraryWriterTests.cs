using Jellyfin.Plugin.HAnimeTV.Library;
using Xunit;

namespace Jellyfin.Plugin.HAnimeTV.Tests
{
    public sealed class LibraryWriterTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hanime-writer-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static LibraryFile File(string path, string content = "x") => new(path.Replace('/', Path.DirectorySeparatorChar), content);

        private string At(string path) => Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

        [Fact]
        public void Write_WritesOnlyWhatChanged()
        {
            var first = LibraryWriter.Write(_root, [File("A/tvshow.nfo"), File("A/Season 01/A S01E01.strm", "link")], CancellationToken.None);
            var modified = System.IO.File.GetLastWriteTimeUtc(At("A/tvshow.nfo"));
            var second = LibraryWriter.Write(_root, [File("A/tvshow.nfo"), File("A/Season 01/A S01E01.strm", "new link")], CancellationToken.None);

            Assert.Equal(new WriteResult(2, 0, 0), first);
            Assert.Equal(new WriteResult(1, 1, 0), second);
            Assert.Equal(modified, System.IO.File.GetLastWriteTimeUtc(At("A/tvshow.nfo")));
            Assert.Equal("new link", System.IO.File.ReadAllText(At("A/Season 01/A S01E01.strm")));
        }

        [Fact]
        public void Write_RemovesWhatIsGone()
        {
            LibraryWriter.Write(_root, [File("A/tvshow.nfo"), File("A/Season 01/A S01E01.strm"), File("A/Season 01/A S01E01.nfo"), File("A/Season 01/A S01E02.strm"), File("B/tvshow.nfo"), File("B/Season 01/B S01E01.strm")], CancellationToken.None);
            // Files Jellyfin may save: a series image and an episode image
            System.IO.File.WriteAllText(At("A/poster.jpg"), "img");
            System.IO.File.WriteAllText(At("A/Season 01/A S01E01-thumb.jpg"), "img");
            System.IO.File.WriteAllText(At("A/Season 01/A S01E02-thumb.jpg"), "img");

            var result = LibraryWriter.Write(_root, [File("A/tvshow.nfo"), File("A/Season 01/A S01E01.strm"), File("A/Season 01/A S01E01.nfo")], CancellationToken.None);

            Assert.Equal(2, result.Deleted);
            Assert.False(Directory.Exists(At("B")));
            Assert.False(System.IO.File.Exists(At("A/Season 01/A S01E02.strm")));
            Assert.False(System.IO.File.Exists(At("A/Season 01/A S01E02-thumb.jpg")));
            Assert.True(System.IO.File.Exists(At("A/Season 01/A S01E01-thumb.jpg")));
            Assert.True(System.IO.File.Exists(At("A/poster.jpg")));
        }

        [Fact]
        public void Write_RefusesFoldersThatAreNotTheLibrarys()
        {
            Directory.CreateDirectory(_root);
            System.IO.File.WriteAllText(Path.Combine(_root, "movie.mkv"), "mine");

            Assert.Throws<InvalidOperationException>(() => LibraryWriter.Write(_root, [File("A/tvshow.nfo")], CancellationToken.None));
            Assert.True(System.IO.File.Exists(Path.Combine(_root, "movie.mkv")));
        }

        [Fact]
        public void Write_StaysInsideTheFolder() =>
            Assert.Throws<InvalidOperationException>(() => LibraryWriter.Write(_root, [File("../escape.strm")], CancellationToken.None));
    }
}

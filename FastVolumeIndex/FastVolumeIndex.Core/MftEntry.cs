using System.IO;

namespace FastVolumeIndex
{
    public sealed class MftEntry
    {
        internal MftEntry(ulong id, ulong parentId, string name, FileAttributes attributes)
        {
            Id = id;
            ParentId = parentId;
            Name = name;
            Attributes = attributes;
        }

        public ulong Id { get; }
        public ulong ParentId { get; }
        public string Name { get; }
        public FileAttributes Attributes { get; }
        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    }
}

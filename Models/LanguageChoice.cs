using System.Windows.Media;
namespace DesktopIniManager.Models
{
        internal sealed class LanguageChoice
        {
            public LanguageChoice(string code, ImageSource flag, string name)
            {
                Code = code; Flag = flag; Name = name;
            }
            public string Code { get; }
            public ImageSource Flag { get; }
            public string Name { get; }
        }
}

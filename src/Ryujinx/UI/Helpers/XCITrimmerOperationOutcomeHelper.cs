using Ryujinx.Ava.Common.Locale;
using static Ryujinx.Common.Utilities.XCIFileTrimmer;

namespace Ryujinx.Ava.UI.Helpers
{
    public static class XCIFileTrimmerOperationOutcomeExtensions
    {
        extension(OperationOutcome opOutcome)
        {
            public string LocalizedText => opOutcome switch
            {
                OperationOutcome.NoTrimNecessary => LocaleManager.Instance[LocaleKeys.TrimXCIFileNoTrimNecessary],
                OperationOutcome.NoUntrimPossible => LocaleManager.Instance[LocaleKeys.TrimXCIFileNoUntrimPossible],
                OperationOutcome.ReadOnlyFileCannotFix => LocaleManager.Instance[
                    LocaleKeys.TrimXCIFileReadOnlyFileCannotFix],
                OperationOutcome.FreeSpaceCheckFailed => LocaleManager.Instance[
                    LocaleKeys.TrimXCIFileFreeSpaceCheckFailed],
                OperationOutcome.InvalidXCIFile => LocaleManager.Instance[LocaleKeys.TrimXCIFileInvalidXCIFile],
                OperationOutcome.FileIOWriteError => LocaleManager.Instance[LocaleKeys.TrimXCIFileFileIOWriteError],
                OperationOutcome.FileSizeChanged => LocaleManager.Instance[LocaleKeys.TrimXCIFileFileSizeChanged],
                OperationOutcome.Cancelled => LocaleManager.Instance[LocaleKeys.TrimXCIFileCancelled],
                OperationOutcome.Undetermined => LocaleManager.Instance[LocaleKeys.TrimXCIFileFileUndertermined],
                _ => null
            };
        }
    }
}

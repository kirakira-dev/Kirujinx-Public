using Avalonia.Platform.Storage;
using Gommon;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Utilities
{
    public static class StorageProviderExtensions
    {
        extension(IStorageProvider storageProvider)
        {
            public Task<Optional<IStorageFolder>> OpenSingleFolderPickerAsync(FolderPickerOpenOptions openOptions = null) =>
                storageProvider.OpenFolderPickerAsync(FixOpenOptions(openOptions, false))
                    .Then(folders => folders.FindFirst());
            
            public Task<Optional<IStorageFile>> OpenSingleFilePickerAsync(FilePickerOpenOptions openOptions = null) =>
                storageProvider.OpenFilePickerAsync(FixOpenOptions(openOptions, false))
                    .Then(files => files.FindFirst());
            
            public Task<Optional<IReadOnlyList<IStorageFolder>>> OpenMultiFolderPickerAsync(FolderPickerOpenOptions openOptions = null) =>
                storageProvider.OpenFolderPickerAsync(FixOpenOptions(openOptions, true))
                    .Then(folders => folders.Count > 0 ? Optional.Of(folders) : default);
            
            public Task<Optional<IReadOnlyList<IStorageFile>>> OpenMultiFilePickerAsync(FilePickerOpenOptions openOptions = null) =>
                storageProvider.OpenFilePickerAsync(FixOpenOptions(openOptions, true))
                    .Then(files => files.Count > 0 ? Optional.Of(files) : default);
        }

        private static FilePickerOpenOptions FixOpenOptions(this FilePickerOpenOptions openOptions, bool allowMultiple)
        {
            if (openOptions is null)
                return new FilePickerOpenOptions { AllowMultiple = allowMultiple };

            openOptions.AllowMultiple = allowMultiple;

            return openOptions;
        }

        private static FolderPickerOpenOptions FixOpenOptions(this FolderPickerOpenOptions openOptions, bool allowMultiple)
        {
            if (openOptions is null)
                return new FolderPickerOpenOptions { AllowMultiple = allowMultiple };

            openOptions.AllowMultiple = allowMultiple;

            return openOptions;
        }
    }
}

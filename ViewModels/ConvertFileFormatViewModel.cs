using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class ConvertFileFormatViewModel : ObservableObject
    {
        private readonly FileViewModel _file;
        private readonly Func<Task<StorageFile>> _pickFile;

        public ConvertFileFormatViewModel(FileViewModel file, Func<Task<StorageFile>> pickFile = null)
        {
            _file = file;
            _pickFile = pickFile ?? (() => SaveFilePickerHelper.PickAsync(Path.GetFileNameWithoutExtension(file.Name), ".pdf"));
        }

        public bool CanConvert => !IsBusy && _file.CanConvert;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public string FormattedExtensions => string.Join(", ", FileConversionRules.Extensions.Select(extension => extension.TrimStart('.')));

        [RelayCommand(CanExecute = nameof(CanConvert))]
        public async Task ConvertFileFormat()
        {
            if (!CanConvert) return;
            IsBusy = true;
            ErrorMessage = string.Empty;
            StatusMessage = string.Empty;
            try
            {
                StorageFile file = await _pickFile();
                if (file == null) return;
                await _file.Drive.Provider.ConvertFileFormat(_file.Id, file);
                SavedFilePath = file.Path;
                StatusMessage = "FileConvert_Completed".GetLocalized();
            }
            catch (Exception exception)
            {
                ErrorMessage = FileOperationErrors.GetMessage(exception);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConvertFileFormatCommand))]
        private bool _isBusy;
        [ObservableProperty] private string _savedFilePath = string.Empty;
        [ObservableProperty] private string _statusMessage = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _errorMessage = string.Empty;
    }
}
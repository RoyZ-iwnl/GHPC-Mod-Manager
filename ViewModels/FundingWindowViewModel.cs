using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Services;

namespace GHPC_Mod_Manager.ViewModels;

/// <summary>
/// 赞助者名单窗口ViewModel：从IFundingService拉取ID列表供滚动展示
/// </summary>
public partial class FundingWindowViewModel : ObservableObject
{
    private readonly IFundingService _fundingService;
    private readonly ILoggingService _loggingService;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private List<string> _sponsors = new();

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasContent = false;

    public IRelayCommand? CloseCommand { get; set; }

    public FundingWindowViewModel(IFundingService fundingService, ILoggingService loggingService)
    {
        _fundingService = fundingService;
        _loggingService = loggingService;
    }

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            HasContent = false;
            ErrorMessage = string.Empty;

            var ids = await _fundingService.GetFundingIdsAsync();
            Sponsors = ids;

            if (ids.Count > 0)
            {
                HasContent = true;
                _loggingService.LogInfo(Strings.FundingLoaded, ids.Count);
            }
            else
            {
                HasContent = false;
                _loggingService.LogInfo(Strings.FundingEmpty);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FundingLoadFailed);
            ErrorMessage = Strings.FundingLoadFailed;
            HasContent = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

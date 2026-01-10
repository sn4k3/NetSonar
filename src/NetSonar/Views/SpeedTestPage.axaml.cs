using NetSonar.Avalonia.Controls;
using NetSonar.Avalonia.ViewModels;

namespace NetSonar.Avalonia.Views;

public partial class SpeedTestPage : UserControlBase
{
    private SpeedTestPageModel? _model;

    public SpeedTestPage()
    {
        InitializeComponent();
    }

    protected override void OnInitialized()
    {
        if (DataContext is SpeedTestPageModel result)
        {
            _model = result;
            _model.SetControls(ResultsGrid);
        }

        base.OnInitialized();

    }
}
namespace Zapret.App.Views;

/// <summary>A view that rebuilds itself from AppState when it becomes visible.</summary>
public interface IRefreshableView
{
    void RefreshView();
}

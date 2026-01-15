namespace RiskWeb.Services;

public class ModuleStateService
{
    private string _currentModule = "Liq";
    private bool _sidebarCollapsed = false;
    private string? _selectedNodeId = null;

    public event Action? OnModuleChanged;
    public event Action? OnSidebarToggled;
    public event Action? OnSelectedNodeChanged;

    public string CurrentModule
    {
        get => _currentModule;
        set
        {
            if (_currentModule != value)
            {
                _currentModule = value;
                OnModuleChanged?.Invoke();
            }
        }
    }

    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set
        {
            if (_sidebarCollapsed != value)
            {
                _sidebarCollapsed = value;
                OnSidebarToggled?.Invoke();
            }
        }
    }

    public string? SelectedNodeId
    {
        get => _selectedNodeId;
        set
        {
            if (_selectedNodeId != value)
            {
                _selectedNodeId = value;
                OnSelectedNodeChanged?.Invoke();
            }
        }
    }

    public void ToggleSidebar()
    {
        SidebarCollapsed = !SidebarCollapsed;
    }
}

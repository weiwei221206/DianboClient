namespace Dianbo.App.Services;

internal readonly record struct TaskbarLyricPopupAnchor(
    int CoverX, int TaskbarTop, int TaskbarBottom,
    int MonitorLeft, int MonitorTop, int MonitorRight, int MonitorBottom,
    float Dpi);

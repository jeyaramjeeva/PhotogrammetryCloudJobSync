namespace PhotogrammetryCloudJobSync;

internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Get()
    {
        if (_cached != null)
            return _cached;

        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
            {
                // Clone so file handle is not kept locked
                using var fileIcon = new Icon(icoPath);
                _cached = (Icon)fileIcon.Clone();
                return _cached;
            }
        }
        catch
        {
            // fall through
        }

        _cached = SystemIcons.Application;
        return _cached;
    }
}

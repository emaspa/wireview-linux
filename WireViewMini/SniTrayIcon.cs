#if !WINDOWS
using System.Runtime.Versioning;
using System.Text;
using Tmds.DBus.Protocol;
using WireView2.Net;

namespace WireView2.Mini;

/// <summary>
/// Linux tray icon: a StatusNotifierItem (org.kde.StatusNotifierItem at
/// /StatusNotifierItem) with its menu exported as com.canonical.dbusmenu at
/// /MenuBar, on the session bus, under the name
/// <c>org.kde.StatusNotifierItem-&lt;pid&gt;-1</c>. KDE, XFCE, Cinnamon, LXQt and
/// GNOME with the AppIndicator extension show it; they all read the menu over
/// dbusmenu.
///
/// The name is taken before registering, so a watcher that scans the bus finds a
/// named item. The item registers with org.kde.StatusNotifierWatcher whenever a
/// watcher owns that name (again after the watcher restarts). With no session
/// bus or no watcher it logs one line and keeps trying (connection every 30 s,
/// registration as soon as a watcher appears and every 30 s after a refusal), so
/// the icon shows up when the desktop's tray comes up after WireView Mini.
/// Behaviour follows Avalonia.FreeDesktop's DBusTrayIconImpl/DBusMenuExporter
/// (property set, name before registration); the code is written from the specs.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class SniTrayIcon : ITrayIcon
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string LayoutSignature = "(ia{sv}av)";
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    private readonly Action<TrayCommand> _onCommand;
    private readonly string _busName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
    private readonly List<(int Width, int Height, byte[] Argb)> _pixmaps = TrayIconImage.SniPixmaps(16, 24, 32, 48, 64);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Task _run;
    private TrayState _state;
    private uint _revision = 1;
    private DBusConnection? _connection;   // set while connected and named

    public SniTrayIcon(TrayState initial, Action<TrayCommand> onCommand)
    {
        _state = initial;
        _onCommand = onCommand;
        _run = Task.Run(() => RunAsync(_cts.Token));
    }

    // ---- State ----

    public void SetState(TrayState state)
    {
        lock (_lock)
        {
            var old = _state;
            if (old == state) return;
            _state = state;
            if (_connection is not { } c) return;

            if (old.Title != state.Title) EmitSignal(c, ItemPath, ItemInterface, "NewTitle");
            if (old.Title != state.Title || old.DetailLine != state.DetailLine)
                EmitSignal(c, ItemPath, ItemInterface, "NewToolTip");
            if (old.Fault != state.Fault)
            {
                string status = StatusOf(state);
                EmitSignal(c, ItemPath, ItemInterface, "NewStatus", "s", (ref MessageWriter w) => w.WriteString(status));
            }

            var oldItems = TrayState.Flatten(old.Items).ToList();
            var newItems = TrayState.Flatten(state.Items).ToList();
            // Lines added or removed (the Remote hosts submenu): a new layout.
            if (!Structure(old.Items).SequenceEqual(Structure(state.Items)))
            {
                uint layoutRev = ++_revision;
                EmitSignal(c, MenuPath, MenuInterface, "LayoutUpdated", "ui", (ref MessageWriter w) =>
                {
                    w.WriteUInt32(layoutRev);
                    w.WriteInt32(0);
                });
                return;
            }
            var changed = newItems.Where((item, i) => item.Line != oldItems[i].Line).Select(i => i.Line).ToList();
            if (changed.Count == 0) return;
            _revision++;
            EmitSignal(c, MenuPath, MenuInterface, "ItemsPropertiesUpdated", "a(ia{sv})a(ias)", (ref MessageWriter w) =>
            {
                var updated = w.WriteArrayStart(DBusType.Struct);
                foreach (var item in changed)
                {
                    w.WriteStructureStart();
                    w.WriteInt32(item.Id);
                    w.WriteDictionary(ItemProperties(item, []));
                }
                w.WriteArrayEnd(updated);
                var removed = w.WriteArrayStart(DBusType.Struct);
                w.WriteArrayEnd(removed);
            });
            // A checkbox flip or an item enabled or disabled (rare) also resets the
            // layout, for hosts that only follow LayoutUpdated. The header and remote
            // host lines, which change every second, go through
            // ItemsPropertiesUpdated only.
            if (changed.Any(i => i.Kind != TrayItemKind.Header))
            {
                uint rev = _revision;
                EmitSignal(c, MenuPath, MenuInterface, "LayoutUpdated", "ui", (ref MessageWriter w) =>
                {
                    w.WriteUInt32(rev);
                    w.WriteInt32(0);
                });
            }
        }
    }

    private static string StatusOf(TrayState s) => s.Fault ? "NeedsAttention" : "Active";

    /// <summary>The menu's shape: each line's parent, id and kind, in order.</summary>
    private static IEnumerable<(int Parent, int Id, TrayItemKind Kind)> Structure(IReadOnlyList<TrayItem> items, int parent = 0)
    {
        foreach (var item in items)
        {
            yield return (parent, item.Id, item.Kind);
            if (item.Children != null)
                foreach (var child in Structure(item.Children, item.Id)) yield return child;
        }
    }

    // ---- Connection and registration ----

    private async Task RunAsync(CancellationToken ct)
    {
        bool loggedNoBus = false;
        while (!ct.IsCancellationRequested)
        {
            DBusConnection? connection = null;
            try
            {
                string address = SessionBusAddress()
                    ?? throw new InvalidOperationException("DBUS_SESSION_BUS_ADDRESS is not set and there is no $XDG_RUNTIME_DIR/bus");
                connection = new DBusConnection(address);
                await connection.ConnectAsync();
                connection.AddMethodHandler(new Handler(this, ItemPath));
                connection.AddMethodHandler(new Handler(this, MenuPath));
                // Own the name first: a watcher that appears later scans the bus
                // and must find the item already named.
                await connection.RequestNameAsync(_busName);
                lock (_lock) _connection = connection;
                if (loggedNoBus) FileLog.Info("tray: connected to the session bus");
                loggedNoBus = false;
                await WatchAndRegisterAsync(connection, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!loggedNoBus)
                    FileLog.Info($"tray: no session D-Bus ({ex.Message}): running without a tray icon, retrying every {Retry.TotalSeconds:0} s");
                loggedNoBus = true;
            }
            finally
            {
                lock (_lock) _connection = null;
                connection?.Dispose();
            }

            try { await Task.Delay(Retry, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Registers with every watcher that owns the name, until the
    /// connection drops (returns) or the icon is disposed (throws).</summary>
    private async Task WatchAndRegisterAsync(DBusConnection connection, CancellationToken ct)
    {
        using var watcher = await connection.WatchNameOwnerAsync(WatcherName);
        var disconnected = connection.DisconnectedAsync();
        bool loggedNoWatcher = false, loggedRefused = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? owner = watcher.GetCurrentOwner();
            bool registered = false;
            if (owner == null)
            {
                if (!loggedNoWatcher)
                    FileLog.Info("tray: no StatusNotifierWatcher on the session bus (no tray on this desktop; " +
                                 "GNOME needs the AppIndicator extension): running without a tray icon until one appears");
                loggedNoWatcher = true;
            }
            else
            {
                try
                {
                    await RegisterAsync(connection);
                    registered = true;
                    loggedNoWatcher = loggedRefused = false;
                    FileLog.Info($"tray: icon registered with the StatusNotifierWatcher as {_busName}");
                    lock (_lock)
                    {
                        // Hosts that cached an earlier item of this name refetch.
                        EmitSignal(connection, ItemPath, ItemInterface, "NewIcon");
                        EmitSignal(connection, ItemPath, ItemInterface, "NewTitle");
                        EmitSignal(connection, ItemPath, ItemInterface, "NewToolTip");
                    }
                }
                catch (DBusErrorReplyException ex)
                {
                    if (!loggedRefused)
                        FileLog.Info($"tray: the StatusNotifierWatcher refused the icon ({ex.ErrorName}: {ex.Message}); " +
                                     $"retrying every {Retry.TotalSeconds:0} s");
                    loggedRefused = true;
                }
            }

            // Wait for the watcher to change (a new one does not know the item),
            // the 30 s retry, the connection to drop, or Dispose.
            using var wake = owner != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, watcher.GetOwnerChangedCancellationToken(owner))
                : CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!registered) wake.CancelAfter(Retry);
            Task waitOwner = owner != null
                ? Task.Delay(Timeout.Infinite, wake.Token)
                : watcher.WaitForOwnerAsync(wake.Token);
            var done = await Task.WhenAny(waitOwner, disconnected);
            if (done == disconnected)
            {
                FileLog.Info("tray: session bus connection lost, reconnecting");
                return;
            }
            try { await waitOwner; } catch (OperationCanceledException) { }
        }
    }

    private Task RegisterAsync(DBusConnection connection)
    {
        MessageBuffer Create()
        {
            var w = connection.GetMessageWriter();
            try
            {
                w.WriteMethodCallHeader(WatcherName, "/StatusNotifierWatcher", WatcherName,
                    "RegisterStatusNotifierItem", "s", MessageFlags.None);
                w.WriteString(_busName);
                return w.CreateMessage();
            }
            finally { w.Dispose(); }
        }
        return connection.CallMethodAsync(Create());
    }

    private static string? SessionBusAddress()
    {
        if (DBusAddress.Session is { Length: > 0 } address) return address;
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime)) runtime = $"/run/user/{geteuid()}";
        string bus = Path.Combine(runtime, "bus");
        return File.Exists(bus) ? $"unix:path={bus}" : null;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint geteuid();

    // ---- Signals ----

    private delegate void BodyWriter(ref MessageWriter writer);

    private static void EmitSignal(DBusConnection c, string path, string iface, string member,
                                   string? signature = null, BodyWriter? body = null)
    {
        var w = c.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(null, path, iface, member, signature);
            body?.Invoke(ref w);
            c.TrySendMessage(w.CreateMessage());
        }
        finally { w.Dispose(); }
    }

    // ---- Method calls ----

    private sealed class Handler(SniTrayIcon owner, string path) : IPathMethodHandler
    {
        public string Path => path;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try
            {
                if (path == ItemPath) owner.HandleItem(context);
                else owner.HandleMenu(context);
            }
            catch (Exception ex)
            {
                if (!context.ReplySent && !context.NoReplyExpected)
                    context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
            }
            return default;
        }
    }

    private void HandleItem(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([ItemXml], ReadOnlySpan<string>.Empty);
            return;
        }
        if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context, ItemInterface, ItemPropertyNames, WriteItemProperty);
            return;
        }
        if (request.InterfaceAsString != ItemInterface)
        {
            context.ReplyUnknownMethodError();
            return;
        }
        switch (request.MemberAsString)
        {
            case "Activate":
                Dispatch(TrayCommand.OpenApp);
                ReplyEmpty(context);
                break;
            case "SecondaryActivate":
            case "ContextMenu":
            case "Scroll":
                ReplyEmpty(context);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void HandleMenu(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([MenuXml], ReadOnlySpan<string>.Empty);
            return;
        }
        if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context, MenuInterface, MenuPropertyNames, WriteMenuProperty);
            return;
        }
        if (request.InterfaceAsString != MenuInterface)
        {
            context.ReplyUnknownMethodError();
            return;
        }

        TrayState state;
        uint revision;
        lock (_lock)
        {
            state = _state;
            revision = _revision;
        }
        var items = state.Items;
        var all = TrayState.Flatten(items).ToList();
        var reader = request.GetBodyReader();
        switch (request.MemberAsString)
        {
            case "GetLayout":
            {
                int parentId = reader.ReadInt32();
                int depth = reader.ReadInt32();
                string[] names = reader.ReadArrayOfString();
                var parent = parentId == 0 ? null : all.FirstOrDefault(i => i.Id == parentId);
                if (parentId != 0 && parent == null)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"no menu item {parentId}");
                    return;
                }
                var w = context.CreateReplyWriter("u" + LayoutSignature);
                try
                {
                    w.WriteUInt32(revision);
                    WriteLayout(ref w, parent, items, depth, names);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "GetGroupProperties":
            {
                int[] ids = reader.ReadArrayOfInt32();
                string[] names = reader.ReadArrayOfString();
                var w = context.CreateReplyWriter("a(ia{sv})");
                try
                {
                    var array = w.WriteArrayStart(DBusType.Struct);
                    foreach (int id in ids)
                    {
                        var item = id == 0 ? null : all.FirstOrDefault(i => i.Id == id);
                        if (id != 0 && item == null) continue;
                        w.WriteStructureStart();
                        w.WriteInt32(id);
                        w.WriteDictionary(ItemProperties(item, names));
                    }
                    w.WriteArrayEnd(array);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "GetProperty":
            {
                int id = reader.ReadInt32();
                string name = reader.ReadString();
                var item = id == 0 ? null : all.FirstOrDefault(i => i.Id == id);
                var value = ItemProperties(item, [name]).Select(p => (VariantValue?)p.Value).FirstOrDefault();
                if ((id != 0 && item == null) || value is not { } v)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"no property {name} on item {id}");
                    return;
                }
                var w = context.CreateReplyWriter("v");
                try
                {
                    w.WriteVariant(v);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "Event":
            {
                int id = reader.ReadInt32();
                string eventId = reader.ReadString();
                ReplyEmpty(context);
                OnEvent(all, id, eventId);
                break;
            }
            case "EventGroup":
            {
                var events = new List<(int, string)>();
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    int id = reader.ReadInt32();
                    string eventId = reader.ReadString();
                    reader.ReadVariantValue();
                    reader.ReadUInt32();
                    events.Add((id, eventId));
                }
                var w = context.CreateReplyWriter("ai");
                try
                {
                    w.WriteArray(events.Select(e => e.Item1).Where(id => all.All(i => i.Id != id)).ToArray());
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                foreach (var (id, eventId) in events) OnEvent(all, id, eventId);
                break;
            }
            case "AboutToShow":
            {
                var w = context.CreateReplyWriter("b");
                try
                {
                    w.WriteBool(false);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "AboutToShowGroup":
            {
                var w = context.CreateReplyWriter("aiai");
                try
                {
                    w.WriteArray(Array.Empty<int>());
                    w.WriteArray(Array.Empty<int>());
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void OnEvent(IReadOnlyList<TrayItem> items, int id, string eventId)
    {
        if (eventId != "clicked") return;
        var item = items.FirstOrDefault(i => i.Id == id);
        if (item is { Kind: TrayItemKind.Action or TrayItemKind.Check, IsEnabled: true } && Enum.IsDefined((TrayCommand)id))
            Dispatch((TrayCommand)id);
    }

    /// <summary>Runs a menu command off the bus's dispatch thread (it may start a
    /// process or stop WireView Mini).</summary>
    private void Dispatch(TrayCommand command)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _onCommand(command); }
            catch (Exception ex) { FileLog.Warn($"tray: {command} failed: {ex.Message}"); }
        });
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        var w = context.CreateReplyWriter(null);
        try { context.Reply(w.CreateMessage()); }
        finally { w.Dispose(); }
    }

    // ---- dbusmenu layout ----

    /// <summary>One (ia{sv}av) node: the root (null, id 0) with the items as
    /// children, a submenu with its lines, or a leaf item. A negative depth is
    /// unlimited.</summary>
    private static void WriteLayout(ref MessageWriter w, TrayItem? item, IReadOnlyList<TrayItem> items,
                                    int depth, string[] names)
    {
        w.WriteStructureStart();
        w.WriteInt32(item?.Id ?? 0);
        w.WriteDictionary(ItemProperties(item, names));
        var children = w.WriteArrayStart(DBusType.Variant);
        var lines = item == null ? items : item.Children;
        if (lines != null && depth != 0)
        {
            foreach (var child in lines)
            {
                w.WriteSignature(LayoutSignature); // a variant: its signature, then the value
                WriteLayout(ref w, child, items, depth - 1, names);
            }
        }
        w.WriteArrayEnd(children);
    }

    /// <summary>dbusmenu properties of an item (the root when null), filtered to
    /// <paramref name="names"/> unless it is empty.</summary>
    private static List<KeyValuePair<string, VariantValue>> ItemProperties(TrayItem? item, string[] names)
    {
        var props = new List<KeyValuePair<string, VariantValue>>();
        void Add(string key, VariantValue value)
        {
            if (names.Length == 0 || names.Contains(key)) props.Add(new(key, value));
        }

        if (item == null)
        {
            Add("children-display", VariantValue.String("submenu"));
            return props;
        }
        if (item.Kind == TrayItemKind.Separator)
        {
            Add("type", VariantValue.String("separator"));
            Add("visible", VariantValue.Bool(true));
            return props;
        }
        Add("label", VariantValue.String(item.Label));
        Add("enabled", VariantValue.Bool(item.IsEnabled));
        Add("visible", VariantValue.Bool(true));
        if (item.Kind == TrayItemKind.Submenu)
            Add("children-display", VariantValue.String("submenu"));
        if (item.Kind == TrayItemKind.Check)
        {
            Add("toggle-type", VariantValue.String("checkmark"));
            Add("toggle-state", VariantValue.Int32(item.Checked ? 1 : 0));
        }
        return props;
    }

    // ---- Properties ----

    private delegate bool PropertyWriter(ref MessageWriter w, string name);

    private static readonly string[] ItemPropertyNames =
    [
        "Category", "Id", "Title", "Status", "WindowId", "IconThemePath", "IconName", "IconPixmap",
        "OverlayIconName", "OverlayIconPixmap", "AttentionIconName", "AttentionIconPixmap",
        "AttentionMovieName", "ToolTip", "ItemIsMenu", "Menu",
    ];

    private static readonly string[] MenuPropertyNames = ["Version", "TextDirection", "Status", "IconThemePath"];

    private void HandleProperties(MethodContext context, string iface, string[] names, PropertyWriter write)
    {
        var request = context.Request;
        var reader = request.GetBodyReader();
        switch (request.MemberAsString)
        {
            case "Get":
            {
                string requestedIface = reader.ReadString();
                string name = reader.ReadString();
                if (requestedIface != iface || !names.Contains(name))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"no property {requestedIface}.{name}");
                    return;
                }
                var w = context.CreateReplyWriter("v");
                try
                {
                    write(ref w, name);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "GetAll":
            {
                string requestedIface = reader.ReadString();
                var w = context.CreateReplyWriter("a{sv}");
                try
                {
                    var dict = w.WriteDictionaryStart();
                    if (requestedIface == iface)
                    {
                        foreach (var name in names)
                        {
                            w.WriteDictionaryEntryStart();
                            w.WriteString(name);
                            write(ref w, name);
                        }
                    }
                    w.WriteDictionaryEnd(dict);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "Set":
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "read-only property");
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    /// <summary>Writes an SNI property as a variant.</summary>
    private bool WriteItemProperty(ref MessageWriter w, string name)
    {
        TrayState s;
        lock (_lock) s = _state;
        switch (name)
        {
            case "Category": w.WriteVariantString("Hardware"); return true;
            case "Id": w.WriteVariantString("wireview-mini"); return true;
            case "Title": w.WriteVariantString(s.Title); return true;
            case "Status": w.WriteVariantString(StatusOf(s)); return true;
            case "WindowId": w.WriteVariantInt32(0); return true;
            case "IconThemePath":
            case "IconName":
            case "OverlayIconName":
            case "AttentionIconName":
            case "AttentionMovieName":
                w.WriteVariantString(""); return true;
            case "IconPixmap":
            case "AttentionIconPixmap":
                w.WriteSignature("a(iiay)");
                WritePixmaps(ref w, _pixmaps);
                return true;
            case "OverlayIconPixmap":
                w.WriteSignature("a(iiay)");
                WritePixmaps(ref w, []);
                return true;
            case "ToolTip":
                w.WriteSignature("(sa(iiay)ss)");
                w.WriteStructureStart();
                w.WriteString("");
                WritePixmaps(ref w, []);
                w.WriteString(TrayState.AppTitle);
                w.WriteString(s.DetailLine ?? "");
                return true;
            case "ItemIsMenu": w.WriteVariantBool(true); return true;
            case "Menu": w.WriteSignature("o"); w.WriteObjectPath(MenuPath); return true;
            default: return false;
        }
    }

    private static bool WriteMenuProperty(ref MessageWriter w, string name)
    {
        switch (name)
        {
            case "Version": w.WriteVariantUInt32(4); return true;
            case "TextDirection": w.WriteVariantString("ltr"); return true;
            case "Status": w.WriteVariantString("normal"); return true;
            case "IconThemePath": w.WriteSignature("as"); w.WriteArray(ReadOnlySpan<string>.Empty); return true;
            default: return false;
        }
    }

    private static void WritePixmaps(ref MessageWriter w, List<(int Width, int Height, byte[] Argb)> pixmaps)
    {
        var array = w.WriteArrayStart(DBusType.Struct);
        foreach (var (width, height, argb) in pixmaps)
        {
            w.WriteStructureStart();
            w.WriteInt32(width);
            w.WriteInt32(height);
            w.WriteArray(argb);
        }
        w.WriteArrayEnd(array);
    }

    // ---- Introspection ----

    private static readonly ReadOnlyMemory<byte> ItemXml = Encoding.UTF8.GetBytes("""
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewTitle"/>
          <signal name="NewIcon"/>
          <signal name="NewAttentionIcon"/>
          <signal name="NewOverlayIcon"/>
          <signal name="NewToolTip"/>
          <signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface>
        """);

    private static readonly ReadOnlyMemory<byte> MenuXml = Encoding.UTF8.GetBytes("""
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg name="parentId" type="i" direction="in"/>
            <arg name="recursionDepth" type="i" direction="in"/>
            <arg name="propertyNames" type="as" direction="in"/>
            <arg name="revision" type="u" direction="out"/>
            <arg name="layout" type="(ia{sv}av)" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg name="ids" type="ai" direction="in"/>
            <arg name="propertyNames" type="as" direction="in"/>
            <arg name="properties" type="a(ia{sv})" direction="out"/>
          </method>
          <method name="GetProperty">
            <arg name="id" type="i" direction="in"/>
            <arg name="name" type="s" direction="in"/>
            <arg name="value" type="v" direction="out"/>
          </method>
          <method name="Event">
            <arg name="id" type="i" direction="in"/>
            <arg name="eventId" type="s" direction="in"/>
            <arg name="data" type="v" direction="in"/>
            <arg name="timestamp" type="u" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg name="events" type="a(isvu)" direction="in"/>
            <arg name="idErrors" type="ai" direction="out"/>
          </method>
          <method name="AboutToShow">
            <arg name="id" type="i" direction="in"/>
            <arg name="needUpdate" type="b" direction="out"/>
          </method>
          <method name="AboutToShowGroup">
            <arg name="ids" type="ai" direction="in"/>
            <arg name="updatesNeeded" type="ai" direction="out"/>
            <arg name="idErrors" type="ai" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated">
            <arg name="updatedProps" type="a(ia{sv})"/>
            <arg name="removedProps" type="a(ias)"/>
          </signal>
          <signal name="LayoutUpdated">
            <arg name="revision" type="u"/>
            <arg name="parent" type="i"/>
          </signal>
          <signal name="ItemActivationRequested">
            <arg name="id" type="i"/>
            <arg name="timestamp" type="u"/>
          </signal>
        </interface>
        """);

    public void Dispose()
    {
        _cts.Cancel();
        DBusConnection? c;
        lock (_lock) c = _connection;
        c?.Dispose(); // drops the name: the watcher removes the item
        try { _run.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
#endif

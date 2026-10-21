using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TestDbHub.Core;
using TestDbHub.Core.Engines;

namespace TestDbHub.Wpf.ViewModels;

public abstract partial class TreeNode : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public abstract string Title { get; }
    public virtual string Icon => "";
    public virtual string? Badge => null;
    public virtual string? ToolTip => null;
    public ObservableCollection<TreeNode> Children { get; } = [];
}

public sealed class EngineNode(DbEngine engine, EngineStatus status) : TreeNode
{
    public DbEngine Engine { get; } = engine;
    public EngineStatus Status { get; } = status;
    public override string Title => Engine.Label;
    public override string Icon => Status.Ok ? "●" : "○";
    public override string? Badge => Status.Ok ? null : "mất kết nối";
    public override string? ToolTip => Status.Ok ? Status.Version : Status.Error;
    public bool IsHelios => Engine.Id == "helios";
    public IEnumerable<DatabaseNode> Databases => Children.OfType<DatabaseNode>();
}

public sealed class DatabaseNode(EngineNode engine, DatabaseInfo info) : TreeNode
{
    public EngineNode EngineNode { get; } = engine;
    public DatabaseInfo Info { get; } = info;
    public override string Title => Info.Name;
    public override string Icon => "🗄";
    public override string? Badge => Info.BranchCount > 0 ? $"{Info.BranchCount} nhánh" : null;
    public IEnumerable<BranchNode> Branches => Children.OfType<BranchNode>();
}

public sealed class BranchNode(DatabaseNode database, BranchInfo info) : TreeNode
{
    public DatabaseNode DatabaseNode { get; } = database;
    public BranchInfo Info { get; } = info;
    public DbEngine Engine => DatabaseNode.EngineNode.Engine;
    public string Database => DatabaseNode.Info.Name;
    public bool IsMain => Info.Kind == BranchKind.Main;
    public override string Title => Info.Name;
    public override string Icon => Info.Kind switch { BranchKind.Main => "★", BranchKind.Snapshot => "📸", _ => "⑂" };
    public override string? Badge => Info.Kind switch { BranchKind.Main => "chính", BranchKind.Snapshot => "snapshot", _ => null };
    public override string? ToolTip => Info.CreatedAt is DateTime t
        ? $"Tạo lúc {t:yyyy-MM-dd HH:mm:ss}{(Info.Parent is null ? "" : " từ " + Info.Parent)}"
        : Info.Parent is null ? null : "Tạo từ " + Info.Parent;
}

namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 树同级节点排序规约（无 UI 依赖，供仓库加载与视图层建树复用，保证两处顺序一致）：
// 1. 目录在前（Folder=0），会话在后（Session=1）；
// 2. 按 SortOrder 升序；
// 3. 名称走自然排序（数字段按数值，如 pve-3 < pve-11 < pve-31），忽略大小写；
//    asciiFirst 为 true 时，首字符为 ASCII 的排在中文等非 ASCII 之前。
public static class TreeNodeOrdering
{
    public static int Compare(TreeNodeBase a, TreeNodeBase b, bool asciiFirst = true)
    {
        var typeCmp = a.NodeType.CompareTo(b.NodeType);
        if (typeCmp != 0)
        {
            return typeCmp;
        }

        var orderCmp = a.SortOrder.CompareTo(b.SortOrder);
        if (orderCmp != 0)
        {
            return orderCmp;
        }

        var nameA = a.Name ?? string.Empty;
        var nameB = b.Name ?? string.Empty;

        if (asciiFirst && nameA.Length > 0 && nameB.Length > 0)
        {
            var aIsAscii = nameA[0] < 128;
            var bIsAscii = nameB[0] < 128;
            if (aIsAscii && !bIsAscii)
            {
                return -1;
            }

            if (!aIsAscii && bIsAscii)
            {
                return 1;
            }
        }

        return NaturalStringComparer.Instance.Compare(nameA, nameB);
    }

    // 原地排序同级节点；少于两项时直接跳过
    public static void Sort(List<TreeNodeBase> siblings, bool asciiFirst = true)
    {
        if (siblings.Count > 1)
        {
            siblings.Sort((a, b) => Compare(a, b, asciiFirst));
        }
    }
}

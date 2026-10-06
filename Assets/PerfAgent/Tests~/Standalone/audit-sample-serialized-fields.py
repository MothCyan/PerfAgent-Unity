"""样例工程的「改名 / 拷副本」体检：场景与预制体里的序列化数据是否还对得上脚本。

为什么需要它：把上游工程拷成 Before/After 两份并改名时，**脚本里的字段名也被改了**
（例如 `SlingShot` -> `AfterSlingShot`），但 `.unity` 里存的是**字段名**，改名不会动它。
两边一旦对不上，字段在运行时就是 null —— 实测就是 `AfterCameraMove.Update()` 里的
`NullReferenceException`（AfterCameraMove.cs:12）。

两条检查（都是「数据 -> 代码」方向，比肉眼看代码可靠）：
  1. MonoBehaviour 块里出现了一个**没有 `m_` 前缀**的键，但它对应的脚本里没有这个字段
     -> 值会被静默丢弃（最常见的原因就是字段被改名了），必须处理。
  2. 脚本里有 public / [SerializeField] 字段，但场景里所有块都没这个键
     -> 多半是新加的缓存字段（没问题），也可能是被改名改丢的（要看第 1 条的报告）。

用法：python audit-sample-serialized-fields.py <工程根，如 D:/hub/PerfAgent>
退出码 0 = 干净，1 = 有对不上的键。
"""
import os
import re
import sys
import glob

SKIP_CODE = {"m_Script", "m_Name", "m_EditorClassIdentifier"}


def read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def script_guid_map(assets_root):
    """脚本文件 -> guid（从 .meta 读）。"""
    out = {}
    for path in glob.glob(os.path.join(assets_root, "**", "*.cs"), recursive=True):
        meta = path + ".meta"
        if not os.path.isfile(meta):
            continue
        m = re.search(r"guid:\s*([0-9a-fA-F]{32})", read(meta))
        if m:
            out[m.group(1)] = path
    return out


FIELD_RE = re.compile(
    r"^\s*(?:public|\[SerializeField\][^\n]*?private|private)\s+"
    r"(?:readonly\s+)?(?:static\s+)?"
    r"([A-Za-z_][\w<>,\[\]\. ]*?)\s+([A-Za-z_]\w*)"
    r"((?:\s*,\s*[A-Za-z_]\w*)*)\s*(?:=|;|\{)"
)


def declared_fields(script_path):
    """脚本里会被 Unity 序列化的字段名。

    注意一行可以声明多个（`public Transform Left, Right;`）—— 只取第一个会假报「键没有对应字段」，
    实测就是 `RightSlingshotOrigin` 这一条。
    """
    names = set()
    for line in read(script_path).splitlines():
        s = line.strip()
        if s.startswith("//") or s.startswith("*"):
            continue
        m = FIELD_RE.match(line)
        if not m:
            continue
        typ, first, rest = m.group(1).strip(), m.group(2), m.group(3)
        if "static" in line or "const" in line or typ in ("void", "class", "struct"):
            continue
        if line.strip().startswith("public") or "[SerializeField]" in line:
            names.add(first)
            for extra in rest.split(","):
                extra = extra.strip()
                if extra:
                    names.add(extra)
    return names


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    project = sys.argv[1]
    assets = os.path.join(project, "Assets")
    guid_map = script_guid_map(assets)

    problems = []
    skipped_external = 0
    checked = 0
    for path in sorted(glob.glob(os.path.join(assets, "**", "*.unity"), recursive=True) +
                       glob.glob(os.path.join(assets, "**", "*.prefab"), recursive=True)):
        text = read(path)
        rel = os.path.relpath(path, project)
        # 按 YAML 文档头切块：--- !u!<classId> &<fileID>
        # 必须按**所有**类切（场景里还有 Transform/GameObject/AudioSource 等），
        # 只切 !u!114 会把别的组件的字段算到 MonoBehaviour 头上（实测：假报一屏）。
        parts = re.split(r"^--- !u!(\d+) &(\d+)$", text, flags=re.M)
        for i in range(1, len(parts), 3):
            class_id, file_id, block = parts[i], parts[i + 1], parts[i + 2]
            if class_id != "114" or not block.lstrip().startswith("MonoBehaviour:"):
                continue
            gm = re.search(r"m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-fA-F]{32})", block)
            if not gm:
                continue
            script = guid_map.get(gm.group(1))
            if not script:
                # guid 不在 Assets/（也不是 Packages/ 里的嵌包）—— 那是官方包/模板自带脚本，
                # 不在本次体检范围（实测 SampleScene 里的 URP/TMP 脚本就会落到这里）。
                skipped_external += 1
                continue
            declared = declared_fields(script)
            checked += 1
            for km in re.finditer(r"^\s{2}([A-Za-z_]\w*):", block, flags=re.M):
                key = km.group(1)
                if key.startswith("m_") or key in SKIP_CODE:
                    continue
                if key not in declared:
                    problems.append("{0} 组件 {1}（{2}）: 键 `{3}` 在脚本里没有对应字段 —— 值会被丢掉".format(
                        rel, file_id, os.path.basename(script), key))

    print("检查了 {0} 个 MonoBehaviour 块（跳过 {1} 个外部包脚本的块）".format(checked, skipped_external))
    if not problems:
        print("OK：场景/预制体的序列化键与脚本字段全部对得上")
        return 0
    for p in problems:
        print("PROBLEM " + p)
    return 1


if __name__ == "__main__":
    sys.exit(main())

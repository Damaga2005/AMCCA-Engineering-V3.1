import os
import sys

def sanitize_id(path_str):
    # Wix identifier must start with letter/underscore and contain only letters, digits, underscores, periods
    clean = "".join(c if (c.isalnum() or c in "._") else "_" for c in path_str)
    return "id_" + clean

def generate_wix_components(publish_dir, output_file):
    publish_dir = os.path.abspath(publish_dir)
    lines = []
    lines.append('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    lines.append('  <Fragment>')

    # Map each directory to its directory Id
    dir_map = {"": "INSTALLFOLDER"}
    dir_entries = []

    # Collect all subdirectories in order
    all_dirs = []
    for root, dirs, _ in os.walk(publish_dir):
        for d in sorted(dirs):
            full_sub = os.path.join(root, d)
            rel_sub = os.path.relpath(full_sub, publish_dir).replace("\\", "/")
            all_dirs.append(rel_sub)
    all_dirs.sort(key=lambda x: x.count("/"))

    for rel_sub in all_dirs:
        parent_rel = os.path.dirname(rel_sub) if "/" in rel_sub else ""
        parent_id = dir_map[parent_rel]
        dir_name = os.path.basename(rel_sub)
        dir_id = "dir_" + sanitize_id(rel_sub)[3:60]
        dir_map[rel_sub] = dir_id
        dir_entries.append((parent_id, dir_id, dir_name))

    # Output Directory definitions
    for parent_id, dir_id, dir_name in dir_entries:
        lines.append(f'    <DirectoryRef Id="{parent_id}">')
        lines.append(f'      <Directory Id="{dir_id}" Name="{dir_name}" />')
        lines.append('    </DirectoryRef>')

    lines.append('    <ComponentGroup Id="AppComponents">')

    idx = 0
    for root, _, files in os.walk(publish_dir):
        rel_dir = os.path.relpath(root, publish_dir).replace("\\", "/")
        if rel_dir == ".":
            rel_dir = ""
        target_dir_id = dir_map[rel_dir]

        for f in sorted(files):
            idx += 1
            full_path = os.path.join(root, f)
            cmp_id = f"cmp_{idx:05d}"
            file_id = f"fil_{idx:05d}"
            xml_path = full_path.replace("&", "&amp;").replace("\"", "&quot;")
            file_name = f.replace("&", "&amp;").replace("\"", "&quot;")

            lines.append(f'      <Component Id="{cmp_id}" Directory="{target_dir_id}" Bitness="always64">')
            lines.append(f'        <File Id="{file_id}" Name="{file_name}" Source="{xml_path}" KeyPath="yes" />')
            lines.append('      </Component>')

    lines.append('    </ComponentGroup>')
    lines.append('  </Fragment>')
    lines.append('</Wix>')

    with open(output_file, "w", encoding="utf-8") as out:
        out.write("\n".join(lines) + "\n")
    print(f"Generated {output_file} with {idx} files across {len(dir_entries)} subdirectories.")

if __name__ == "__main__":
    p_dir = sys.argv[1] if len(sys.argv) > 1 else "artifacts/publish/win-x64"
    o_file = sys.argv[2] if len(sys.argv) > 2 else "installer/Components.wxs"
    generate_wix_components(p_dir, o_file)

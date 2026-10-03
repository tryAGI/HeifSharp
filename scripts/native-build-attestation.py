#!/usr/bin/env python3
"""Record unsigned, local provenance after a native build or package import."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys

RID_PATTERN = re.compile(r'^(linux|osx|win)-(arm64|x64)$')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def file_record(path, label=None):
    path = Path(path)
    if not path.is_file():
        raise ValueError(f'missing input: {path.name}')
    return {'name': label or path.name, 'sha256': digest(path), 'size': path.stat().st_size}


def source_record(path):
    path = Path(path).resolve()
    if path.is_file():
        return file_record(path)
    if not path.is_dir():
        raise ValueError(f'missing source: {path.name}')
    # Record source/configuration files while excluding generated compiler products.
    files = sorted(p for p in path.rglob('*') if p.is_file()
                   and not set(p.relative_to(path).parts) & {'.git', '.libs', '.deps', 'CMakeFiles', '__pycache__'}
                   and not re.search(r'\.(o|obj|lo|la|a|so(?:\.\d+)*|dylib|dll|exe|pyc)$', p.name))
    if not files:
        raise ValueError(f'empty source tree: {path.name}')
    entries = [file_record(p, p.relative_to(path).as_posix()) for p in files]
    revision = subprocess.run(['git', '-C', str(path), 'rev-parse', 'HEAD'], capture_output=True, text=True)
    return {'name': path.name, 'revision': revision.stdout.strip() if revision.returncode == 0 else None,
            'files': entries, 'tree_sha256': hashlib.sha256(json.dumps(entries, sort_keys=True).encode()).hexdigest()}


def tool_record(command):
    executable = shutil.which(command)
    if executable is None:
        raise ValueError(f'missing tool: {command}')
    result = subprocess.run([executable, '--version'], capture_output=True, text=True, timeout=15)
    if result.returncode:
        raise ValueError(f'cannot query tool version: {command}')
    return {'name': Path(command).name, 'version': (result.stdout or result.stderr).strip()[:4096],
            'executable_sha256': digest(Path(executable).resolve())}


def record(root, rid, kind, started, sources, tools, parameters, command, configs=(), reports=(), image=None):
    root = Path(root).resolve()
    if not RID_PATTERN.fullmatch(rid):
        raise ValueError('unsupported RID')
    if kind not in ('build', 'import') or not command or not sources or not parameters:
        raise ValueError('kind, source inputs, invocation and parameters are required')
    marker = Path(started)
    if not marker.is_file():
        raise ValueError('missing operation start marker')
    baseline = json.loads((root / 'NATIVE_PROVENANCE.json').read_text())
    artifacts = [a for a in baseline['artifacts']
                 if any(p.startswith(f'runtimes/{rid}/native/') for p in a.get('package_paths', []))]
    if not artifacts:
        raise ValueError('no declared native outputs for RID')
    outputs = []
    for artifact in artifacts:
        path = (root / artifact['path']).resolve()
        if not path.is_relative_to(root) or not path.is_file():
            raise ValueError(f'missing output: {artifact["path"]}')
        if path.stat().st_mtime_ns <= marker.stat().st_mtime_ns:
            raise ValueError(f'stale output: {artifact["path"]}')
        description = subprocess.check_output(['file', '-b', str(path)], text=True).strip()
        system, architecture = rid.split('-')
        formats = {'linux': 'ELF', 'osx': 'Mach-O', 'win': 'PE32'}
        cpu_tokens = {'arm64': ('arm64', 'aarch64'), 'x64': ('x86-64', 'x86_64', 'AMD64')}
        if formats[system] not in description or not any(t in description for t in cpu_tokens[architecture]):
            raise ValueError(f'output architecture/format does not match {rid}: {artifact["path"]}')
        outputs.append({'path': artifact['path'], 'sha256': digest(path), 'size': path.stat().st_size,
                        'format': description})
    toolchain = [tool_record(t) for t in tools]
    target_reports = []
    for directory in reports:
        directory = Path(directory)
        for path in sorted(directory.glob('*.txt')):
            if path.stat().st_mtime_ns <= marker.stat().st_mtime_ns:
                raise ValueError('stale target toolchain report')
            target_reports.append({**file_record(path), 'report': path.read_text()[:16384], 'report_truncated': path.stat().st_size > 16384})
    if not toolchain and not target_reports:
        raise ValueError('toolchain evidence is required')
    toolchain.append(tool_record('file'))
    if reports and not re.fullmatch(r'sha256:[0-9a-f]{64}', image or ''):
        raise ValueError('immutable container image ID is required for target reports')
    receipt = {'schema_version': 1, 'recorder': file_record(Path(__file__)), 'kind': kind, 'rid': rid,
               'baseline_manifest_sha256': digest(root / 'NATIVE_PROVENANCE.json'),
               'recorded_at': datetime.now(timezone.utc).isoformat(),
               'assurance': 'unsigned local observation; not an independent reproducibility or security attestation',
               'command': command, 'parameters': parameters, 'sources': [source_record(s) for s in sources],
               'toolchain': toolchain, 'target_toolchain_reports': target_reports, 'container_image': image,
               'configuration_files': [file_record(c) for c in configs], 'outputs': outputs}
    if shutil.which('dpkg-query'):
        packages = subprocess.run(['dpkg-query', '-W'], capture_output=True, text=True, timeout=15)
        if packages.returncode == 0:
            receipt['system_packages'] = packages.stdout.splitlines()
    if kind == 'import':
        receipt['upstream_compiler_provenance'] = 'unknown; local tools perform import/extraction, not upstream compilation'
    folder = root / 'natives/attestations' / rid
    folder.mkdir(parents=True, exist_ok=True)
    data = json.dumps(receipt, indent=2, sort_keys=True).encode() + b'\n'
    target = folder / (hashlib.sha256(data).hexdigest() + '.json')
    temporary = target.with_suffix('.tmp')
    temporary.write_bytes(data)
    temporary.replace(target)
    return target


def check(root, receipt):
    root = Path(root).resolve()
    if receipt.get('schema_version') != 1 or not RID_PATTERN.fullmatch(receipt.get('rid', '')):
        raise ValueError('invalid receipt schema/RID')
    if receipt.get('kind') not in ('build', 'import') or not receipt.get('command') or not receipt.get('parameters'):
        raise ValueError('missing operation metadata')
    if not receipt.get('sources') or not (receipt.get('toolchain') or receipt.get('target_toolchain_reports')):
        raise ValueError('missing input/toolchain evidence')
    if not receipt.get('outputs'):
        raise ValueError('empty output inventory')
    for entry in receipt['outputs']:
        path = (root / entry['path']).resolve()
        if not path.is_relative_to(root) or not path.is_file() or digest(path) != entry['sha256']:
            raise ValueError(f'output differs from receipt: {entry["path"]}')
    return receipt['kind']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--rid', required=True)
    parser.add_argument('--kind', choices=['build', 'import'], required=True)
    parser.add_argument('--started', type=Path, required=True)
    parser.add_argument('--source', action='append', required=True)
    parser.add_argument('--tool', action='append', default=[])
    parser.add_argument('--parameter', action='append', required=True)
    parser.add_argument('--configuration', action='append', default=[])
    parser.add_argument('--tool-report-directory', action='append', default=[])
    parser.add_argument('--container-image')
    parser.add_argument('--command', required=True)
    args = parser.parse_args()
    try:
        path = record(args.root, args.rid, args.kind, args.started, args.source, args.tool,
                      args.parameter, args.command, args.configuration, args.tool_report_directory, args.container_image)
        print(f'Native {args.kind} provenance: {path.relative_to(args.root.resolve())}')
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Cannot record provenance: {error}\n')


if __name__ == '__main__':
    main()

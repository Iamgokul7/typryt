# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec for the packaged engine (one folder: typryt-engine\typryt-engine.exe + _internal\...).
# Built by installer\build_release.ps1 (and python-engine\build_engine.ps1). Every data file the engine reads is listed here;
# the build script runs the built engine and fails if one is missing.
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

datas = [
    ('dictionaries', 'dictionaries'),   # Hunspell en_US.aff / en_US.dic (read through spylls)
    ('lexicons', 'lexicons'),           # tech, indian, names, places, brands words and the curated misspelling map
]
datas += collect_data_files('symspellpy')   # the word-frequency list and the bigram list
hiddenimports = ['uvicorn.logging', 'uvicorn.loops.auto', 'uvicorn.protocols.http.auto', 'uvicorn.protocols.websockets.auto', 'uvicorn.lifespan.on']
hiddenimports += collect_submodules('uvicorn')
hiddenimports += collect_submodules('spylls')

a = Analysis(
    ['engine_entry.py'],
    pathex=['.'],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='typryt-engine',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name='typryt-engine',
)

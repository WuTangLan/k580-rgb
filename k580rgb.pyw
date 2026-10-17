"""Windowless launcher (pythonw k580rgb.pyw): runs K580 RGB in the system tray.

Used by run.bat and by "Start with Windows", which can't rely on the working directory.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from k580rgb.__main__ import main  # noqa: E402

main()

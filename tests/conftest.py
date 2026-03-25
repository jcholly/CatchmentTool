"""Shared pytest configuration for CatchmentTool tests."""

import pytest


def pytest_addoption(parser):
    parser.addoption(
        "--runslow", action="store_true", default=False, help="Run slow stress tests"
    )


def pytest_configure(config):
    config.addinivalue_line("markers", "slow: mark test as slow (use --runslow to run)")


def pytest_collection_modifyitems(config, items):
    if config.getoption("--runslow"):
        return
    skip_slow = pytest.mark.skip(reason="Use --runslow to run")
    for item in items:
        if "slow" in item.keywords:
            item.add_marker(skip_slow)

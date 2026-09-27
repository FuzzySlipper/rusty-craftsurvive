#!/usr/bin/env python3
"""One instrument for reading and asserting on the live creature lane.

Every round of S4 that used shell pipelines to read the creature readout lost its
measurement to a silently empty field. This script exists so that never happens
again: it parses named fields, and it *throws with the raw text* when a field is
missing, so a parse failure can never be mistaken for a product value.

    scripts/probe-creatures.py --origin http://127.0.0.1:37328 read
    scripts/probe-creatures.py --origin ... teleport-near 1 20
    scripts/probe-creatures.py --origin ... verify-engagement 1 20 --wait 7 --wait 9
"""

import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.request

HEAD = re.compile(r'tick=(?P<tick>\d+); active=(?P<active>\d+); entities=(?P<entities>\d+); '
                  r'seed=\d+; nav=(?P<nav>.+?) cells=(?P<cells>\d+) revision=(?P<revision>\d+)')
ROW = re.compile(r'id=(?P<id>\d+) entity=EntityId \{ Value = \d+ \} '
                 r'at=(?P<x>-?[\d.]+),(?P<z>-?[\d.]+) state=(?P<state>\w+) '
                 r'hp=(?P<hp>\d+)/(?P<maximum>\d+) d=(?P<distance>[\d.]+) route=(?P<route>\S+)')
FLOATS = ('x', 'z', 'distance')
INTS = ('hp', 'maximum')


def invoke(origin, line):
    request = urllib.request.Request(
        origin.rstrip('/') + '/__rusty/product/runtime/debug/execute',
        data=line.encode(),
        headers={'content-type': 'text/plain; charset=utf-8'},
        method='POST')
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors='replace')


def snapshot(origin):
    status, text = invoke(origin, 'craft.creatures.readout')
    if status != 200:
        raise SystemExit(f'readout answered HTTP {status}: {text[:200]}')
    head = HEAD.search(text)
    if head is None:
        raise SystemExit(f'readout header unparsed: {text[:200]}')
    creatures = {}
    for match in ROW.finditer(text):
        fields = match.groupdict()
        creatures[int(fields['id'])] = {
            key: float(fields[key]) if key in FLOATS else int(fields[key]) if key in INTS else fields[key]
            for key in ('id', 'x', 'z', 'state', 'hp', 'maximum', 'distance', 'route')}
    if not creatures:
        raise SystemExit(f'no creature rows parsed: {text[:300]}')
    return {'tick': int(head.group('tick')), 'nav': head.group('nav'), 'cells': int(head.group('cells')),
            'creatureCount': len(creatures), 'creatures': creatures}


def standing_height(origin):
    """The player's own y, so a test never places the controller inside terrain.

    Teleporting to a fixed height above the ground put the controller into
    unresolved penetration, the character step threw every frame, the exception
    escaped a product callback and the Engine tainted the runtime - which cost
    five rounds of lost measurements.
    """
    status, text = invoke(origin, 'craft.player.readout')
    if status != 200:
        raise SystemExit(f'player readout answered HTTP {status}: {text[:200]}')
    match = re.search(r'after=[-0-9.]+,([-0-9.]+),[-0-9.]+', text)
    if match is None:
        raise SystemExit(f'player position unparsed: {text[:200]}')
    return float(match.group(1))


def one(origin, identifier):
    state = snapshot(origin)
    if identifier not in state['creatures']:
        raise SystemExit(f'creature {identifier} is not in the readout: {sorted(state["creatures"])}')
    return state, state['creatures'][identifier]


def command_read(args):
    state = snapshot(args.origin)
    state.pop('creatures')
    print(json.dumps(state, indent=1))
    return 0


def command_teleport_near(args):
    state, creature = one(args.origin, args.id)
    height = standing_height(args.origin) if args.height is None else args.height
    status, text = invoke(args.origin, f"craft.player.teleport {creature['x']} {height} {creature['z'] + args.metres}")
    if status != 200:
        raise SystemExit(f'teleport answered HTTP {status}: {text[:160]}')
    print(json.dumps({'creature': creature, 'height': height, 'teleport': text[:80]}, indent=1))
    return 0


def command_verify_engagement(args):
    before, creature = one(args.origin, args.id)
    print(json.dumps({'before': creature}, indent=1))
    height = standing_height(args.origin) if args.height is None else args.height
    status, text = invoke(args.origin, f"craft.player.teleport {creature['x']} {height} {creature['z'] + args.metres}")
    if status != 200:
        raise SystemExit(f'teleport answered HTTP {status}: {text[:160]}')
    time.sleep(args.wait[0])
    _, first = one(args.origin, args.id)
    time.sleep(args.wait[1])
    _, second = one(args.origin, args.id)
    print(json.dumps({'first': first, 'second': second}, indent=1))
    if first['state'] == 'Idle':
        raise SystemExit(f"not verified: creature stayed idle at d={first['distance']}")
    if not second['distance'] < first['distance']:
        raise SystemExit(f"not verified: distance did not close ({first['distance']} -> {second['distance']})")
    if second['route'].startswith(('Start', 'outofbox', 'noanswer', 'unasked')):
        raise SystemExit(f"not verified: route is not definitive ({second['route']})")
    print(f"SURVIVED ENGAGEMENT AND CLOSED: {first['distance']} -> {second['distance']}, "
          f"route {second['route']}, state {second['state']}, box cells {before['cells']}")
    return 0


parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument('--origin', required=True)
sub = parser.add_subparsers(dest='command', required=True)
sub.add_parser('read').set_defaults(handler=command_read)
near = sub.add_parser('teleport-near')
near.add_argument('id', type=int)
near.add_argument('metres', type=float)
near.add_argument('--height', type=float, default=None)
near.set_defaults(handler=command_teleport_near)
engagement = sub.add_parser('verify-engagement')
engagement.add_argument('id', type=int)
engagement.add_argument('metres', type=float)
engagement.add_argument('--height', type=float, default=None)
engagement.add_argument('--wait', type=float, nargs=2, default=[7.0, 9.0])
engagement.set_defaults(handler=command_verify_engagement)
args = parser.parse_args()
sys.exit(args.handler(args))

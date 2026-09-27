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

HEAD = re.compile(r'tick=(?P<tick>\d+); active=(?P<active>\d+); entities=(?P<entities>\d+);'
                  r'.*?nav=(?P<nav>.+?) cells=(?P<cells>\d+)')
ROW = re.compile(r'id=(?P<id>\d+) entity=EntityId \{ Value = \d+ \} '
                 r'at=(?P<x>-?[\d.]+),(?P<z>-?[\d.]+) state=(?P<state>\w+) '
                 r'hp=(?P<hp>\d+)/(?P<maximum>\d+) d=(?P<distance>[\d.]+) route=(?P<route>\S+)')
ENTITIES = re.compile(r'entities=(\d+)')
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
        # An empty world is a legitimate answer, not a parse failure: a cleared
        # encounter leaves no rows. The readout states the expected count, so a
        # zero count with zero rows is trusted, while rows that fail to parse
        # still raise.
        if ENTITIES.search(text) is not None and int(ENTITIES.search(text).group(1)) == 0:
            return {'tick': int(head.group('tick')), 'nav': head.group('nav'), 'cells': int(head.group('cells')),
                    'creatureCount': 0, 'creatures': {}}
        raise SystemExit(f'no creature rows parsed: {text[:300]}')
    return {'tick': int(head.group('tick')), 'nav': head.group('nav'), 'cells': int(head.group('cells')),
            'creatureCount': len(creatures), 'creatures': creatures}


def destination_height(origin, x, z):
    """The surface at the destination column, read from the product itself.

    Teleporting to the *player's* height put the controller inside terrain
    whenever the destination ground was higher, and the character step then threw
    every frame. The product already reports the surface of the column it
    occupies, so the probe asks instead of assuming: land on the column, read
    `surface` from `craft.encounter.readout`, and stand one above it.
    """
    invoke(origin, f'craft.player.teleport {x} 100 {z}')
    time.sleep(1.0)
    status, text = invoke(origin, 'craft.encounter.readout')
    if status != 200:
        raise SystemExit(f'encounter readout answered HTTP {status}: {text[:200]}')
    match = re.search(r'surface=(-?\d+)', text)
    if match is None:
        raise SystemExit(f'destination surface unparsed: {text[:200]}')
    return float(match.group(1)) + 1.0


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
    target_z = creature['z'] + args.metres
    height = args.height if args.height is not None else destination_height(args.origin, creature['x'], target_z)
    status, text = invoke(args.origin, f"craft.player.teleport {creature['x']} {height} {target_z}")
    if status != 200:
        raise SystemExit(f'teleport answered HTTP {status}: {text[:160]}')
    print(json.dumps({'creature': creature, 'destinationHeight': height, 'teleport': text[:80]}, indent=1))
    return 0


def parse_player(text):
    """The player line the readout carries, or a loud failure."""
    match = re.search(r'player=(?P<health>\d+)/(?P<maximum>\d+) defeats=(?P<defeats>\d+) outcome=(?P<outcome>\w+)', text)
    if match is None:
        raise SystemExit(f'player fields unparsed: {text[:240]}')
    fields = match.groupdict()
    return {'health': int(fields['health']), 'maximum': int(fields['maximum']),
            'defeats': int(fields['defeats']), 'outcome': fields['outcome']}


def command_verify_death_loop(args):
    """Asserts the whole loop: a death, a respawn at half health, and no repeat."""
    state, creature = one(args.origin, args.id)
    print(json.dumps({'before': creature, 'player': state.get('player')}, indent=1))
    height = args.height if args.height is not None else destination_height(args.origin, creature['x'], creature['z'] + args.metres)
    status, text = invoke(args.origin, f"craft.player.teleport {creature['x']} {height} {creature['z'] + args.metres}")
    if status != 200:
        raise SystemExit(f'teleport answered HTTP {status}: {text[:160]}')

    defeats = 0
    aliveAfterDeath = False
    stable = 0
    for attempt in range(args.polls):
        time.sleep(args.wait)
        _, body = invoke(args.origin, 'craft.creatures.readout')
        player = parse_player(body)
        print(f"poll {attempt + 1}: health={player['health']}/{player['maximum']} "
              f"defeats={player['defeats']} outcome={player['outcome']}")
        if player['defeats'] > defeats:
            defeats = player['defeats']
        elif defeats > 0 and player['outcome'] == 'Alive' and player['health'] > 0:
            aliveAfterDeath = True
            stable += 1

    if defeats == 0:
        raise SystemExit('not verified: the chase never defeated the player')
    if not aliveAfterDeath:
        raise SystemExit(f'not verified: the player never came back after {defeats} defeat(s)')
    if stable < args.stable:
        raise SystemExit(f'not verified: only {stable} stable polls after the respawn, wanted {args.stable}')
    print(f"DEATH LOOP VERIFIED: {defeats} defeat(s), respawned to health>0 and stable for {stable} polls")
    return 0


def command_verify_engagement(args):
    before, creature = one(args.origin, args.id)
    print(json.dumps({'before': creature}, indent=1))
    target_z = creature['z'] + args.metres
    height = args.height if args.height is not None else destination_height(args.origin, creature['x'], target_z)
    status, text = invoke(args.origin, f"craft.player.teleport {creature['x']} {height} {target_z}")
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
loop = sub.add_parser('verify-death-loop')
loop.add_argument('id', type=int)
loop.add_argument('metres', type=float)
loop.add_argument('--height', type=float, default=None)
loop.add_argument('--wait', type=float, default=5.0)
loop.add_argument('--polls', type=int, default=12)
loop.add_argument('--stable', type=int, default=2)
loop.set_defaults(handler=command_verify_death_loop)
args = parser.parse_args()
sys.exit(args.handler(args))

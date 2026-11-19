"""Draw Grok's eyes as patches on a sphere, then bake the motion into Android vectors."""

from dataclasses import astuple, dataclass, replace
from math import atan2, ceil, cos, pi, radians, remainder, sin, sqrt
from pathlib import Path
import sys

from scipy.interpolate import PchipInterpolator

ANDROID = 'xmlns:android="http://schemas.android.com/apk/res/android"'
AAPT = 'xmlns:aapt="http://schemas.android.com/aapt"'
CENTER, RADIUS, LENGTH = 128, 109, 2680
EYE_RADIUS = 13.2
EYE_STRAIGHT = 15.5
EYE_SPACING = 27.8
EYE_HEIGHT = -17.8
CAMERA_DEPTH = 8 * RADIUS
FOCAL_LENGTH = sqrt(CAMERA_DEPTH**2 - RADIUS**2)
LIMB_DEPTH = RADIUS**2 / CAMERA_DEPTH


@dataclass(frozen=True)
class Pose:
    yaw: float = 0
    pitch: float = 10
    left: float = 1
    right: float = 1


REST = Pose()
LEFT, RIGHT = Pose(-31, 11), Pose(31, 11)
CROWN_STEPS = 10
SIDE_STEPS = 6
CAPSULE = (
    *((EYE_RADIUS*cos(pi + i*pi/CROWN_STEPS), -EYE_STRAIGHT + EYE_RADIUS*sin(pi + i*pi/CROWN_STEPS))
      for i in range(CROWN_STEPS + 1)),
    *((EYE_RADIUS, -EYE_STRAIGHT + 2*EYE_STRAIGHT*i/SIDE_STEPS) for i in range(1, SIDE_STEPS)),
    *((EYE_RADIUS*cos(i*pi/CROWN_STEPS), EYE_STRAIGHT + EYE_RADIUS*sin(i*pi/CROWN_STEPS))
      for i in range(CROWN_STEPS + 1)),
    *((-EYE_RADIUS, EYE_STRAIGHT - 2*EYE_STRAIGHT*i/SIDE_STEPS) for i in range(1, SIDE_STEPS)),
)


def project(x, y, z):
    scale = FOCAL_LENGTH / (CAMERA_DEPTH - z)
    return complex(CENTER + x*scale, CENTER + y*scale)


def limb_point(angle):
    return complex(CENTER + RADIUS*cos(angle), CENTER + RADIUS*sin(angle))


def limb_crossing(a, b):
    fraction = (LIMB_DEPTH - a[2]) / (b[2] - a[2])
    angle = atan2(a[1] + fraction*(b[1]-a[1]), a[0] + fraction*(b[0]-a[0]))
    return fraction, angle


def visible_outline(points):
    """Replace hidden arcs with the silhouette, keeping the same path topology for Android."""
    visible = [z >= LIMB_DEPTH for _, _, z in points]
    if not any(visible):
        x, y, _ = max(points, key=lambda p: p[2])
        return [limb_point(atan2(y, x))] * len(points)

    result = [project(*point) for point in points]
    count = len(points)
    for start in range(count):
        if visible[start] or not visible[start-1]:
            continue
        end = start + 1
        while not visible[end % count]:
            end += 1
        enter_fraction, enter_angle = limb_crossing(points[start-1], points[start])
        leave_fraction, leave_angle = limb_crossing(points[(end-1) % count], points[end % count])
        enter, leave = start - 1 + enter_fraction, end - 1 + leave_fraction
        sweep = remainder(leave_angle - enter_angle, 2*pi)
        for i in range(start, end):
            result[i % count] = limb_point(enter_angle + sweep*(i-enter)/(leave-enter))
    return result


def eye_points(pose, side):
    yaw = radians(pose.yaw % 360)
    pitch = radians(pose.pitch - REST.pitch)
    yaw_cos, yaw_sin = cos(yaw), sin(yaw)
    pitch_cos, pitch_sin = cos(pitch), sin(pitch)
    opening = pose.left if side < 0 else pose.right
    surface = []
    for u, v in CAPSULE:
        lid = 7 + 2.4*(1 - (u/EYE_RADIUS)**2)
        screen_x = side*EYE_SPACING + u
        screen_y = EYE_HEIGHT + opening*v + (1-opening)*lid
        # Intersect the frontal eye's camera ray with the sphere. The symmetric
        # frontal shape is exact, and the whole contour bends when it turns.
        radius_squared = screen_x**2 + screen_y**2
        ray_squared = radius_squared / FOCAL_LENGTH**2
        z = (ray_squared*CAMERA_DEPTH + sqrt(RADIUS**2 - radius_squared)) / (1 + ray_squared)
        lift = (CAMERA_DEPTH-z) / FOCAL_LENGTH
        x, y = screen_x*lift, screen_y*lift
        turned_x = x*yaw_cos + z*yaw_sin
        turned_z = z*yaw_cos - x*yaw_sin
        surface.append((turned_x, y*pitch_cos - turned_z*pitch_sin, y*pitch_sin + turned_z*pitch_cos))
    return visible_outline(surface)


def eye_path(pose, side):
    p = eye_points(pose, side)
    midpoint = [(a+b)/2 for a,b in zip(p,p[1:]+p[:1])]
    def pair(point):
        return f"{point.real:.3f},{point.imag:.3f}"
    return f"M{pair(midpoint[-1])} " + " ".join(
        f"Q{pair(control)} {pair(end)}" for control,end in zip(p,midpoint)
    ) + " Z"


def closed(pose, left=True, right=True, amount=.025):
    return replace(pose, left=amount if left else pose.left, right=amount if right else pose.right)


def clip(*moments, end=REST):
    return ((0, REST), *moments, (LENGTH, end))


def glance(pose, start, arrive, hold, back, blink_at=None, dwell=32):
    lids = blink(blink_at, pose, dwell=dwell) if blink_at is not None else ()
    return clip((start, REST), (arrive, pose), *lids, (hold, pose), (back, REST))


def blink(start, pose=REST, left=True, right=True, dwell=32, close_time=76, reopen_time=118):
    shut = closed(pose, left, right)
    closed_at = start + close_time
    reopen_at = closed_at + dwell
    return ((start, pose), (closed_at, shut), (reopen_at, shut), (reopen_at+reopen_time, pose))


def look_blink(pose, start, left=True, right=True):
    return clip((130, REST), (450, pose), *blink(start, pose, left, right),
                (1860, pose), (2220, REST))


def full_turn(side, start, anticipation, middle, finish, elevated=False):
    rest_after = Pose(side*360, REST.pitch)
    return clip(
        (start, REST), (anticipation, Pose(-side*12, 6)),
        (middle-330, Pose(side*67, 19 if elevated else 9)),
        (middle, Pose(side*178, 31 if elevated else 10)),
        (middle+330, Pose(side*283, 17 if elevated else 5)),
        (finish-220, Pose(side*366, 8)),
        (finish, rest_after), end=rest_after,
    )


CUES = {
    "left": glance(LEFT, 180, 390, 1270, 1850, blink_at=690, dwell=27),
    "right": glance(RIGHT, 365, 680, 1400, 1940),
    "far_left": glance(Pose(-73, 13), 160, 900, 1360, 2260),
    "far_right": glance(Pose(72, 6), 310, 870, 1600, 2340),
    "up": glance(Pose(2, 40), 360, 670, 1430, 1950),
    "down": glance(Pose(-2, -28), 420, 690, 1640, 2330, blink_at=1085, dwell=39),
    "up_left": glance(Pose(-34, 31), 230, 590, 1500, 2150),
    "up_right": glance(Pose(39, 34), 550, 850, 1690, 2250, blink_at=1370, dwell=28),
    "down_left": glance(Pose(-41, -16), 170, 500, 1470, 2030, blink_at=870, dwell=42),
    "down_right": glance(Pose(36, -18), 295, 630, 1360, 2060),
    "small_left": clip((330, REST), (505, Pose(-9, 13)), (795, Pose(-11, 12)),
                       (930, Pose(-16, 15)), (1500, Pose(-16, 14)), (2120, REST)),
    "small_right": clip((170, REST), (330, Pose(12, 10)), (670, Pose(12, 10)),
                        (940, Pose(7, 15)), (1250, Pose(10, 14)),
                        *blink(1510, Pose(10, 14), dwell=26, close_time=71, reopen_time=111),
                        (1990, REST)),
    "sweep_right": full_turn(1, 160, 370, 1240, 2150),
    "sweep_left": full_turn(-1, 230, 460, 1380, 2320),
    "double_take_left": clip((140, REST), (370, Pose(-20, 4)), (700, Pose(-20, 4)),
                             (910, REST), (1170, Pose(-49, 17)), (1660, Pose(-49, 18)),
                             (2220, REST)),
    "double_take_right": full_turn(1, 155, 335, 1240, 2140, True),
    "scan_up_down": clip((220, REST), (510, Pose(-10, 29)), (870, Pose(-9, 27)),
                         (1250, Pose(12, -15)), (1530, Pose(13, -16)), (2130, REST)),
    "blink_early": clip(*blink(515, close_time=72), (1310, Pose(4, 13)), (2220, REST)),
    "blink_late": clip((420, Pose(-4, 11)), (1140, REST), *blink(1600)),
    "double_blink": clip(*blink(505), *blink(1000, dwell=24, close_time=69, reopen_time=111),
                        (2110, REST)),
    "wink_left": clip(*blink(870, right=False, dwell=58)),
    "wink_right": clip(*blink(1120, left=False, dwell=51)),
    "squint": clip((400, REST), (800, closed(Pose(-16, 12), amount=.51)),
                   (1400, closed(Pose(-16, 13), amount=.51)), (2020, REST)),
    "sleepy": clip((435, REST), (930, closed(Pose(0, 3), amount=.22)),
                   (1470, closed(Pose(0, 3), amount=.13)), (2220, REST)),
    "left_then_blink": look_blink(Pose(-48, 14), 1120),
    "right_then_blink": look_blink(Pose(49, 12), 980),
    "down_then_blink": look_blink(Pose(5, -24), 1350),
    "up_then_blink": full_turn(-1, 150, 350, 1280, 2240, True),
    "left_then_wink": look_blink(Pose(-23, 17), 780, right=False),
    "right_then_wink": look_blink(Pose(23, 15), 1140, left=False),
}


def sampled(timeline):
    times = [t for t, _ in timeline]
    curves = PchipInterpolator(times, [astuple(pose) for _, pose in timeline], axis=0)
    result = [(0, REST)]
    for (start, a), (end, b) in zip(timeline, timeline[1:]):
        if a == b:
            count = 1
        else:
            count = max(ceil(abs(b.yaw-a.yaw)/8), ceil(abs(b.pitch-a.pitch)/5),
                        ceil((end-start)/105), ceil(max(abs(b.left-a.left), abs(b.right-a.right))*4), 2)
        for i in range(1, count+1):
            time = round(start + (end-start)*i/count)
            result.append((time, Pose(*map(float, curves(time)))))
    return result


def write(path, content):
    path.write_text(content, encoding="utf-8")


def generate(res):
    header = f'<vector {ANDROID} {AAPT} android:width="64dp" android:height="64dp" android:viewportWidth="256" android:viewportHeight="256">\n'
    sphere = '''    <path android:pathData="M128,19 A109,109 0,1 1,128,237 A109,109 0,1 1,128,19 Z"
        android:strokeColor="#90171717" android:strokeWidth="2.6">
        <aapt:attr name="android:fillColor">
            <gradient android:type="radial" android:centerX="103" android:centerY="87" android:gradientRadius="162">
                <item android:offset="0" android:color="#FFFFFF" />
                <item android:offset="0.57" android:color="#FFFFFF" />
                <item android:offset="1" android:color="#ECEFF3" />
            </gradient>
        </aapt:attr>
    </path>
'''
    sides = (("left_eye", -1), ("right_eye", 1))
    eyes = "".join(f'    <path android:name="{name}" android:fillColor="#151515" android:pathData="{eye_path(REST,side)}" />\n' for name,side in sides)
    for name, body in (("face", sphere), ("eyes", eyes), ("icon", sphere+eyes)):
        write(res/"drawable"/f"grok_{name}.xml", header+body+"</vector>\n")

    moving = set(res.joinpath("drawable").glob("grok_*.xml")) - {
        res/"drawable"/f"grok_{name}.xml" for name in ("face", "eyes", "icon")
    }
    assert {p.stem for p in moving} == {f"grok_{cue}" for cue in CUES}
    for cue, timeline in CUES.items():
        poses = sampled(timeline)
        name = f"grok_{cue}"
        targets = []
        for eye, side in sides:
            frames = [(t, eye_path(pose, side)) for t,pose in poses]
            if all(path == frames[0][1] for _,path in frames):
                continue
            animation = f"{name}_{eye}"
            segments = "\n".join(
                f'    <objectAnimator android:propertyName="pathData" android:valueType="pathType"\n'
                f'        android:duration="{end-start}" android:interpolator="@android:interpolator/linear"\n'
                f'        android:valueFrom="{before}" android:valueTo="{after}" />'
                for (start,before),(end,after) in zip(frames,frames[1:])
            )
            write(res/"animator"/f"{animation}.xml",
                  f'<set {ANDROID} android:ordering="sequentially">\n{segments}\n</set>\n')
            targets.append(f'    <target android:name="{eye}" android:animation="@animator/{animation}" />')
        write(res/"drawable"/f"{name}.xml",
              f'<animated-vector {ANDROID} android:drawable="@drawable/grok_eyes">\n'
              + "\n".join(targets) + "\n</animated-vector>\n")


if __name__ == "__main__":
    generate(Path(sys.argv[1]))

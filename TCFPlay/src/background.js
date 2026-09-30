const SCENE_WIDTH = 1600;
const SCENE_HEIGHT = 900;
const WINDOW_X = [569, 677, 785, 1109, 1217, 1325];
const CLOUDS = [
  [280, 190, 1.15, 0.84, -450, 1500, 34, -8],
  [1160, 270, 0.75, 0.60, -1350, 620, 48, -31],
  [710, 135, 0.58, 0.56, -900, 1070, 41, -19],
  [90, 92, 0.42, 0.42, -270, 1690, 58, -44],
  [1420, 118, 0.48, 0.46, -1600, 360, 63, -13],
  [510, 305, 0.88, 0.72, -700, 1270, 39, -27],
  [940, 350, 0.50, 0.48, -1120, 840, 55, -49],
  [1380, 205, 0.96, 0.76, -1580, 400, 45, -22]
];

export function cloudOffsetAt(elapsedMs, startX, endX, durationSeconds, delaySeconds) {
  const localSeconds = elapsedMs / 1000 - delaySeconds;
  const progress = ((localSeconds % durationSeconds) + durationSeconds) % durationSeconds
    / durationSeconds;
  return startX + (endX - startX) * progress;
}

export function flagWaveAt(elapsedMs) {
  return Math.sin((elapsedMs / 2800) * Math.PI * 2) * 6;
}

export function swingSeatAt(elapsedMs, length, phase = 0) {
  const angle = Math.sin(elapsedMs / 720 + phase) * (8 * Math.PI / 180);
  return {
    angle,
    x: Math.sin(angle) * length,
    y: Math.cos(angle) * length
  };
}

function drawCloud(context, x, y, scale, opacity) {
  context.save();
  context.translate(x, y);
  context.scale(scale, scale);
  context.globalAlpha = opacity;
  context.fillStyle = "#FFF7DF";
  context.beginPath();
  context.ellipse(0, 20, 82, 28, 0, 0, Math.PI * 2);
  context.fill();
  for (const [cx, cy, radius] of [
    [-36, 0, 34],
    [10, -13, 47],
    [51, 5, 31]
  ]) {
    context.beginPath();
    context.arc(cx, cy, radius, 0, Math.PI * 2);
    context.fill();
  }
  context.restore();
}

function drawSun(context, elapsedMs) {
  const pulse = 1 + Math.sin(elapsedMs / 580) * 0.045;
  context.save();
  context.translate(265, 280);
  context.scale(pulse, pulse);
  context.fillStyle = "#FFD45F";
  context.globalAlpha = 0.95;
  context.beginPath();
  context.arc(0, 0, 64, 0, Math.PI * 2);
  context.fill();
  context.globalAlpha = 0.24 + Math.sin(elapsedMs / 580) * 0.06;
  context.strokeStyle = "#FFD45F";
  context.lineWidth = 4;
  context.beginPath();
  context.arc(0, 0, 90, 0, Math.PI * 2);
  context.stroke();
  context.restore();
}

function drawFlag(context, elapsedMs) {
  const wave = flagWaveAt(elapsedMs);
  context.save();
  context.fillStyle = "#E8D7AF";
  context.strokeStyle = "#8F7652";
  context.lineWidth = 1;
  context.beginPath();
  context.roundRect(960, 111, 6, 141, 2.5);
  context.fill();
  context.stroke();
  context.fillStyle = "#F0A533";
  context.beginPath();
  context.arc(963, 108, 6, 0, Math.PI * 2);
  context.fill();
  context.stroke();

  context.translate(966, 119);
  const cloth = new Path2D();
  cloth.moveTo(0, 2);
  cloth.bezierCurveTo(36, -2 + wave, 84, 8 - wave, 132, 1 + wave * 0.25);
  cloth.lineTo(132, 86 + wave * 0.25);
  cloth.bezierCurveTo(84, 91 - wave, 36, 77 + wave, 0, 87);
  cloth.closePath();
  context.save();
  context.clip(cloth);
  context.fillStyle = "#FFFFFF";
  context.fillRect(0, 0, 132, 88);
  context.fillStyle = "#01411C";
  context.fillRect(33, 0, 99, 88);
  context.fillStyle = "#FFFFFF";
  context.beginPath();
  context.arc(82, 44, 22, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = "#01411C";
  context.beginPath();
  context.arc(91, 39, 20, 0, Math.PI * 2);
  context.fill();
  context.fillStyle = "#FFFFFF";
  context.beginPath();
  for (let point = 0; point < 10; point += 1) {
    const radius = point % 2 === 0 ? 11 : 4.5;
    const angle = -Math.PI / 2 + point * Math.PI / 5 - 0.2;
    const x = 112 + Math.cos(angle) * radius;
    const y = 36 + Math.sin(angle) * radius;
    if (point === 0) context.moveTo(x, y);
    else context.lineTo(x, y);
  }
  context.closePath();
  context.fill();
  context.restore();
  context.strokeStyle = "#17352A";
  context.lineWidth = 1.5;
  context.stroke(cloth);
  context.restore();
}

function drawSwings(context, elapsedMs) {
  context.save();
  context.translate(0, 45);
  context.fillStyle = "rgb(23 53 42 / 0.2)";
  context.beginPath();
  context.ellipse(82, 646, 66, 13, -11 * Math.PI / 180, 0, Math.PI * 2);
  context.fill();
  context.strokeStyle = "#8CCB62";
  context.lineWidth = 5;
  context.lineCap = "round";
  context.beginPath();
  context.moveTo(20, 659);
  context.quadraticCurveTo(80, 642, 145, 632);
  context.stroke();

  context.strokeStyle = "#F0A533";
  context.lineWidth = 11;
  context.beginPath();
  context.moveTo(44, 516);
  context.lineTo(126, 495);
  context.stroke();
  context.strokeStyle = "#087541";
  context.lineWidth = 10;
  for (const [x1, y1, x2, y2] of [
    [54, 513, 20, 659],
    [54, 513, 78, 644],
    [116, 498, 91, 641],
    [116, 498, 145, 632]
  ]) {
    context.beginPath();
    context.moveTo(x1, y1);
    context.lineTo(x2, y2);
    context.stroke();
  }

  const seats = [
    { centerX: 75, topY: 508.1, restY: 590, phase: 0 },
    { centerX: 101, topY: 501.4, restY: 581, phase: Math.PI }
  ];
  for (const seat of seats) {
    const length = seat.restY - 5 - seat.topY;
    const motion = swingSeatAt(elapsedMs, length, seat.phase);
    const seatX = seat.centerX + motion.x;
    const seatY = seat.topY + motion.y + 5;
    context.strokeStyle = "#547B76";
    context.lineWidth = 2;
    for (const ropeOffset of [-5, 5]) {
      context.beginPath();
      context.moveTo(seat.centerX + ropeOffset, seat.topY);
      context.lineTo(seatX + ropeOffset, seatY - 5);
      context.stroke();
    }
    context.save();
    context.translate(seatX, seatY);
    context.rotate(motion.angle);
    context.fillStyle = "#D54843";
    context.strokeStyle = "#8F432C";
    context.lineWidth = 2;
    context.beginPath();
    context.roundRect(-9, -4, 18, 6, 2);
    context.fill();
    context.stroke();
    context.restore();
  }
  context.restore();
}

function drawTree(context, x, y, scale, trunkHeight) {
  context.save();
  context.translate(x, y);
  context.scale(scale, scale);
  context.fillStyle = "#8F5A38";
  context.beginPath();
  context.roundRect(-10, 12, 20, trunkHeight, 8);
  context.fill();
  for (const [cx, cy, radius, color] of [
    [0, 0, 52, "#078447"],
    [-34, 18, 35, "#149956"],
    [34, 20, 38, "#0A713E"]
  ]) {
    context.fillStyle = color;
    context.beginPath();
    context.arc(cx, cy, radius, 0, Math.PI * 2);
    context.fill();
  }
  context.restore();
}

function drawWindow(context, x, y, glass) {
  context.fillStyle = glass;
  context.strokeStyle = "#087541";
  context.lineWidth = 6;
  context.fillRect(x + 5, y + 5, 58, 48);
  context.strokeRect(x + 5, y + 5, 58, 48);
  context.lineWidth = 4;
  context.beginPath();
  context.moveTo(x + 34, y + 7);
  context.lineTo(x + 34, y + 51);
  context.stroke();
  context.lineWidth = 3;
  context.beginPath();
  context.moveTo(x + 8, y + 29);
  context.lineTo(x + 60, y + 29);
  context.stroke();
  context.fillStyle = "#E59A70";
  context.strokeStyle = "#8F432C";
  context.lineWidth = 1;
  context.fillRect(x - 5, y - 7, 78, 7);
  context.strokeRect(x - 5, y - 7, 78, 7);
  context.fillStyle = "#D6875D";
  context.fillRect(x - 3, y + 58, 74, 7);
  context.strokeRect(x - 3, y + 58, 74, 7);
}

function drawBrickLines(context, x, y, width, height, brickWidth = 36, brickHeight = 22) {
  context.save();
  context.strokeStyle = "rgb(143 67 44 / 0.42)";
  context.lineWidth = 1;
  for (let rowY = y + brickHeight; rowY < y + height; rowY += brickHeight) {
    context.beginPath();
    context.moveTo(x, rowY);
    context.lineTo(x + width, rowY);
    context.stroke();
  }
  let row = 0;
  for (let rowY = y; rowY < y + height; rowY += brickHeight) {
    const offset = row % 2 ? brickWidth / 2 : 0;
    for (
      let columnX = x + offset;
      columnX < x + width;
      columnX += brickWidth
    ) {
      context.beginPath();
      context.moveTo(columnX, rowY);
      context.lineTo(columnX, Math.min(y + height, rowY + brickHeight));
      context.stroke();
    }
    row += 1;
  }
  context.restore();
}

function drawSchool(context) {
  context.save();
  context.shadowColor = "rgb(23 53 42 / 0.22)";
  context.shadowBlur = 12;
  context.shadowOffsetY = 12;

  context.fillStyle = "#C96E43";
  context.strokeStyle = "#8F432C";
  context.lineWidth = 3;
  context.fillRect(530, 386, 956, 267);
  context.strokeRect(530, 386, 956, 267);
  drawBrickLines(context, 530, 386, 956, 267);

  context.fillStyle = "#BE623D";
  context.fillRect(839, 254, 245, 399);
  context.strokeRect(839, 254, 245, 399);
  drawBrickLines(context, 839, 254, 245, 399);
  context.restore();

  context.fillStyle = "#E59A70";
  context.strokeStyle = "#8F432C";
  context.lineWidth = 1;
  for (const [x, y, width, height] of [
    [530, 648, 956, 8],
    [530, 519, 956, 8],
    [530, 430, 956, 8],
    [530, 386, 344, 8],
    [1052, 386, 434, 8],
    [847, 254, 232, 9]
  ]) {
    context.fillRect(x, y, width, height);
    context.strokeRect(x, y, width, height);
  }

  const glass = context.createLinearGradient(0, 0, 70, 60);
  glass.addColorStop(0, "#A9C8C4");
  glass.addColorStop(1, "#547B76");
  for (const x of WINDOW_X) {
    drawWindow(context, x, 547, glass);
    drawWindow(context, x, 437, glass);
  }

  context.fillStyle = "#087541";
  context.strokeStyle = "#17352A";
  context.lineWidth = 4;
  context.beginPath();
  context.moveTo(918, 653);
  context.lineTo(918, 538);
  context.bezierCurveTo(918, 478, 1008, 478, 1008, 538);
  context.lineTo(1008, 653);
  context.closePath();
  context.fill();
  context.stroke();
  context.strokeStyle = "#17352A";
  context.lineWidth = 3;
  context.beginPath();
  context.moveTo(963, 503);
  context.lineTo(963, 653);
  context.stroke();

  drawWindow(context, 929, 347, glass);

  context.fillStyle = "#F2D8B5";
  context.strokeStyle = "#8F432C";
  context.lineWidth = 3;
  context.beginPath();
  context.roundRect(858, 300, 210, 38, 4);
  context.fill();
  context.stroke();
  context.fillStyle = "#078447";
  context.font = "800 17px Segoe UI, sans-serif";
  context.textAlign = "center";
  context.textBaseline = "middle";
  context.fillText("TCF School Seattle", 963, 320);

  context.fillStyle = "#D6875D";
  context.strokeStyle = "#8F432C";
  context.lineWidth = 1;
  context.fillRect(839, 292, 14, 135);
  context.strokeRect(839, 292, 14, 135);
  context.fillRect(1070, 292, 14, 135);
  context.strokeRect(1070, 292, 14, 135);

  context.fillStyle = "#F2D8B5";
  context.fillRect(884, 653, 158, 15);
  context.fillRect(900, 668, 126, 13);
  context.fillRect(916, 681, 94, 12);

  for (const [x, y, radius, color] of [
    [555, 665, 25, "#078447"],
    [610, 669, 30, "#149956"],
    [1320, 665, 30, "#149956"],
    [1380, 668, 26, "#078447"]
  ]) {
    context.fillStyle = color;
    context.beginPath();
    context.arc(x, y, radius, 0, Math.PI * 2);
    context.fill();
  }
}

export function createCampusBackground(width, height) {
  const skyCanvas = document.createElement("canvas");
  skyCanvas.width = width;
  skyCanvas.height = height;
  const skyContext = skyCanvas.getContext("2d");
  skyContext.scale(width / SCENE_WIDTH, height / SCENE_HEIGHT);

  const sky = skyContext.createLinearGradient(0, 0, 0, SCENE_HEIGHT);
  sky.addColorStop(0, "#68C9F2");
  sky.addColorStop(1, "#D9F3FA");
  skyContext.fillStyle = sky;
  skyContext.fillRect(0, 0, SCENE_WIDTH, SCENE_HEIGHT);

  const foregroundCanvas = document.createElement("canvas");
  foregroundCanvas.width = width;
  foregroundCanvas.height = height;
  const context = foregroundCanvas.getContext("2d");
  context.scale(width / SCENE_WIDTH, height / SCENE_HEIGHT);

  context.fillStyle = "#57BE74";
  context.beginPath();
  context.moveTo(0, 585);
  context.quadraticCurveTo(210, 505, 410, 578);
  context.quadraticCurveTo(610, 651, 810, 568);
  context.quadraticCurveTo(1010, 485, 1210, 552);
  context.quadraticCurveTo(1410, 619, 1600, 565);
  context.lineTo(1600, 900);
  context.lineTo(0, 900);
  context.closePath();
  context.fill();

  const grass = context.createLinearGradient(0, 585, 0, 900);
  grass.addColorStop(0, "#2FAF68");
  grass.addColorStop(1, "#078447");
  context.fillStyle = grass;
  context.beginPath();
  context.moveTo(0, 665);
  context.quadraticCurveTo(230, 590, 435, 661);
  context.quadraticCurveTo(640, 732, 845, 648);
  context.quadraticCurveTo(1050, 564, 1240, 632);
  context.quadraticCurveTo(1430, 700, 1600, 650);
  context.lineTo(1600, 900);
  context.lineTo(0, 900);
  context.closePath();
  context.fill();

  drawTree(context, 230, 520, 1.1, 76);
  drawTree(context, 1420, 590, 1, 98);
  drawTree(context, 1330, 640, 0.65, 98);
  drawSchool(context);
  return {
    draw(target, elapsedMs, reduceMotion = false) {
      target.drawImage(skyCanvas, 0, 0);
      target.save();
      target.scale(width / SCENE_WIDTH, height / SCENE_HEIGHT);
      const animationTime = reduceMotion ? 0 : elapsedMs;
      drawSun(target, animationTime);
      for (const [
        x, y, scale, opacity, startX, endX, duration, delay
      ] of CLOUDS) {
        drawCloud(
          target,
          x + cloudOffsetAt(animationTime, startX, endX, duration, delay),
          y,
          scale,
          opacity
        );
      }
      target.restore();
      target.drawImage(foregroundCanvas, 0, 0);
      target.save();
      target.scale(width / SCENE_WIDTH, height / SCENE_HEIGHT);
      drawSwings(target, animationTime);
      drawFlag(target, animationTime);
      target.restore();
    }
  };
}

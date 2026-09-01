import Phaser from 'phaser';
import Player from '../Player.js';
import {
  VIEW_WIDTH,
  VIEW_HEIGHT,
  WORLD_WIDTH,
  GROUND_Y,
  GROUND_THICKNESS,
  SCENERY,
  CHARACTER_DISPLAY_HEIGHT,
  RENDER_SCALE
} from '../config.js';

const DEPTH = {
  sky: 0,
  clouds: 1,
  hillsFar: 2,
  hillsMid: 3,
  hillsNear: 4,
  props: 5,
  ground: 6,
  shadow: 7,
  player: 8,
  hud: 100
};

export default class GameScene extends Phaser.Scene {
  constructor() {
    super('Game');
  }

  create() {
    const rig = this.cache.json.get('rigMeta');

    // Every object has to belong to exactly one of the two cameras, or it draws
    // twice — once zoomed about the world's centre and once about the corner.
    this.worldObjects = [];
    this.hudObjects = [];

    // Stop him where the *drawing* meets the screen edge, not where his collision
    // box does. Mid-stride his legs and leading arm reach well past the standing
    // silhouette the hitbox was measured from, so colliding on the hitbox alone
    // would push that overhang off screen and clip him in half at the wall.
    const scale = CHARACTER_DISPLAY_HEIGHT / rig.characterHeightPx;
    const overhang = (rig.reach - rig.hitbox.width / 2) * scale;

    this.physics.world.setBounds(
      overhang,
      -2000,
      WORLD_WIDTH - overhang * 2,
      2000 + VIEW_HEIGHT
    );
    this.physics.world.gravity.y = 1800;
    this.cameras.main.setBounds(0, 0, WORLD_WIDTH, VIEW_HEIGHT);
    // The canvas is RENDER_SCALE times bigger than the world, so zoom by the same
    // factor: the camera still shows exactly 1280x720 world units, just rendered
    // at the display's real pixel density.
    this.cameras.main.setZoom(RENDER_SCALE);
    this.cameras.main.setRoundPixels(false);
    this.cameras.main.setBackgroundColor('#000000');

    this.#buildBackdrop();
    this.#buildGround();

    this.player = new Player(this, VIEW_WIDTH / 2, GROUND_Y, rig);
    this.player.setDepth(DEPTH.player);
    this.player.shadow.setDepth(DEPTH.shadow);
    this.player.setCollideWorldBounds(true);
    this.worldObjects.push(this.player, this.player.shadow);

    this.physics.add.collider(this.player, this.groundBody);

    // A world one screen wide has nothing to scroll to, so the camera stays put
    // and the character walks all the way to the visible edge. Widen the world
    // and the camera starts following him again.
    if (WORLD_WIDTH > VIEW_WIDTH) {
      this.cameras.main.startFollow(this.player, false, 0.12, 0.12, 0, 140);
      this.cameras.main.setDeadzone(220, 200);
    }

    this.#buildInput();
    this.#buildHud();
    this.#buildHudCamera();
  }

  /**
   * The HUD gets its own camera because zoom is applied about a camera's centre:
   * a `setScrollFactor(0)` object at (20, 18) under a 3x main camera lands at
   * canvas x = 1920 + (20 - 1920) * 3, which is a long way off the left of the
   * screen. A camera with its origin at the top-left maps world (20, 18) to canvas
   * (60, 54) instead — the same inset the 1x build has, at three times the
   * resolution. It carries no bounds, so nothing can push it off the corner.
   */
  #buildHudCamera() {
    const camera = this.cameras.add(
      0, 0,
      Math.round(VIEW_WIDTH * RENDER_SCALE),
      Math.round(VIEW_HEIGHT * RENDER_SCALE)
    );
    camera.setOrigin(0, 0);
    camera.setZoom(RENDER_SCALE);
    camera.setScroll(0, 0);
    camera.transparent = true;

    camera.ignore(this.worldObjects);
    this.cameras.main.ignore(this.hudObjects);
    this.hudCamera = camera;
  }

  #buildBackdrop() {
    if (!SCENERY) return;

    const sky = this.add
      .image(0, 0, 'sky')
      .setOrigin(0, 0)
      .setScrollFactor(0)
      .setDisplaySize(VIEW_WIDTH, VIEW_HEIGHT)
      .setDepth(DEPTH.sky);

    this.clouds = this.add
      .tileSprite(0, 30, VIEW_WIDTH, 300, 'clouds')
      .setOrigin(0, 0)
      .setScrollFactor(0)
      .setDepth(DEPTH.clouds);

    this.hillsFar = this.#hillLayer('hillsFar', GROUND_Y + 26, DEPTH.hillsFar, 320);
    this.hillsMid = this.#hillLayer('hillsMid', GROUND_Y + 16, DEPTH.hillsMid, 360);
    this.hillsNear = this.#hillLayer('hillsNear', GROUND_Y + 6, DEPTH.hillsNear, 300);

    this.worldObjects.push(sky, this.clouds, this.hillsFar, this.hillsMid, this.hillsNear);
  }

  #hillLayer(key, bottomY, depth, height) {
    return this.add
      .tileSprite(0, bottomY - height, VIEW_WIDTH, height, key)
      .setOrigin(0, 0)
      .setScrollFactor(0)
      .setDepth(depth);
  }

  #buildGround() {
    if (SCENERY) {
      const ground = this.add
        .tileSprite(0, GROUND_Y, WORLD_WIDTH, GROUND_THICKNESS + 160, 'ground')
        .setOrigin(0, 0)
        .setDepth(DEPTH.ground);

      // A continuous rail fence gives the eye a fixed reference, making any foot
      // slide immediately obvious while the character walks past it.
      const props = this.add.graphics().setDepth(DEPTH.props);

      for (let x = 90; x <= WORLD_WIDTH; x += 210) {
        props.fillStyle(0x000000, 0.14);
        props.fillEllipse(x + 9, GROUND_Y + 3, 36, 11);
        props.fillStyle(0x5b3f25, 1);
        props.fillRect(x - 6, GROUND_Y - 86, 12, 90);
        props.fillStyle(0x7c583b, 1);
        props.fillRect(x - 6, GROUND_Y - 86, 5, 90);
      }

      for (const y of [GROUND_Y - 74, GROUND_Y - 40]) {
        props.fillStyle(0x6b4a2c, 1);
        props.fillRect(0, y, WORLD_WIDTH, 9);
        props.fillStyle(0x8a6440, 1);
        props.fillRect(0, y, WORLD_WIDTH, 3);
        props.fillStyle(0x3f2c19, 0.45);
        props.fillRect(0, y + 7, WORLD_WIDTH, 2);
      }

      this.worldObjects.push(ground, props);
    }

    // The floor is physics only. On black there is nothing to draw it against,
    // but the character still has to stand on something.
    const body = this.add.rectangle(
      WORLD_WIDTH / 2,
      GROUND_Y + GROUND_THICKNESS / 2,
      WORLD_WIDTH,
      GROUND_THICKNESS
    );
    body.setVisible(false);
    this.physics.add.existing(body, true);
    this.groundBody = body;
    this.worldObjects.push(body);
  }

  #buildInput() {
    this.cursors = this.input.keyboard.createCursorKeys();
    this.keys = this.input.keyboard.addKeys({
      left: Phaser.Input.Keyboard.KeyCodes.A,
      right: Phaser.Input.Keyboard.KeyCodes.D,
      run: Phaser.Input.Keyboard.KeyCodes.SHIFT
    });
  }

  #buildHud() {
    const style = {
      fontFamily: 'Consolas, Menlo, monospace',
      fontSize: '15px',
      color: '#eaf2ff'
    };

    const help = this.add
      .text(20, 18, 'A / D  or  \u2190 / \u2192   walk        SHIFT   run', style)
      .setScrollFactor(0)
      .setDepth(DEPTH.hud)
      .setResolution(RENDER_SCALE)
      .setShadow(0, 2, '#0d1b2a', 4);

    this.debugText = this.add
      .text(20, VIEW_HEIGHT - 38, '', { ...style, fontSize: '13px', color: '#bcd2ee' })
      .setScrollFactor(0)
      .setDepth(DEPTH.hud)
      .setResolution(RENDER_SCALE)
      .setShadow(0, 2, '#0d1b2a', 4);

    this.hudObjects.push(help, this.debugText);
  }

  update(time, delta) {
    // Cap the step so a browser hitch cannot teleport the character and tear a
    // hole in the walk cycle.
    const dt = Math.min(delta, 1000 / 30) / 1000;

    const left = this.cursors.left.isDown || this.keys.left.isDown;
    const right = this.cursors.right.isDown || this.keys.right.isDown;

    this.player.update(dt, {
      axis: (right ? 1 : 0) - (left ? 1 : 0),
      run: this.cursors.shift.isDown || this.keys.run.isDown
    });

    const scrollX = this.cameras.main.scrollX;
    if (SCENERY) {
      this.clouds.tilePositionX = scrollX * 0.05 + time * 0.004;
      this.hillsFar.tilePositionX = scrollX * 0.18;
      this.hillsMid.tilePositionX = scrollX * 0.34;
      this.hillsNear.tilePositionX = scrollX * 0.56;
    }

    const speed = Math.abs(this.player.body.velocity.x);
    this.debugText.setText(
      `speed ${speed.toFixed(0).padStart(3)} px/s   ` +
        `frame ${this.player.displayFrame}   ` +
        `stride ${this.player.strideWorldPx.toFixed(1)} px   ` +
        `max advance ${this.player.framesPerTick(1 / 60).toFixed(2)} frames/tick @60Hz`
    );
  }
}

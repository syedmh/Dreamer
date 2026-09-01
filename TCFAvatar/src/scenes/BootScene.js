import Phaser from 'phaser';
import { VIEW_WIDTH, VIEW_HEIGHT, SCENERY } from '../config.js';
import { generateBackdropTextures, generateCharacterTextures } from '../backdrop.js';

export default class BootScene extends Phaser.Scene {
  constructor() {
    super('Boot');
  }

  preload() {
    this.#drawLoader();

    this.load.atlas('hero', 'assets/hero/hero.png', 'assets/hero/hero.json');
    this.load.json('rigMeta', 'assets/hero/rig-meta.json');
  }

  create() {
    generateCharacterTextures(this);
    if (SCENERY) generateBackdropTextures(this);
    this.scene.start('Game');
  }

  #drawLoader() {
    const cx = VIEW_WIDTH / 2;
    const cy = VIEW_HEIGHT / 2;

    this.cameras.main.setBackgroundColor('#10131a');

    const label = this.add
      .text(cx, cy - 44, 'Loading', {
        fontFamily: 'Segoe UI, system-ui, sans-serif',
        fontSize: '20px',
        color: '#8fa6c4'
      })
      .setOrigin(0.5);

    const track = this.add.rectangle(cx, cy, 420, 6, 0x263041).setOrigin(0.5);
    const fill = this.add.rectangle(track.x - 210, cy, 0, 6, 0x4da3ff).setOrigin(0, 0.5);

    this.load.on('progress', (value) => {
      fill.width = 420 * value;
    });

    this.load.on('complete', () => {
      label.destroy();
      track.destroy();
      fill.destroy();
    });
  }
}

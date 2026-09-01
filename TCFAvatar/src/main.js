import Phaser from 'phaser';
import BootScene from './scenes/BootScene.js';
import GameScene from './scenes/GameScene.js';
import { VIEW_WIDTH, VIEW_HEIGHT, RENDER_SCALE } from './config.js';

const config = {
  type: Phaser.AUTO,
  parent: 'game',
  // The canvas is sized in *device* pixels and the camera is zoomed to match, so
  // the world is still 1280x720 but the frame is rendered at the panel's own
  // resolution instead of being stretched onto it.
  width: Math.round(VIEW_WIDTH * RENDER_SCALE),
  height: Math.round(VIEW_HEIGHT * RENDER_SCALE),
  backgroundColor: '#000000',
  antialias: true,
  roundPixels: false,
  pixelArt: false,
  scale: {
    mode: Phaser.Scale.FIT,
    autoCenter: Phaser.Scale.CENTER_BOTH
  },
  fps: {
    target: 60,
    min: 30
  },
  physics: {
    default: 'arcade',
    arcade: {
      gravity: { y: 1800 },
      debug: false
    }
  },
  scene: [BootScene, GameScene]
};

// eslint-disable-next-line no-new
const game = new Phaser.Game(config);

// Exposed so the animation can be inspected/verified from the browser console.
window.__game = game;

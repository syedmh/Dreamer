// Fireworks animation using Canvas
class Firework {
    constructor(x, y) {
        this.x = x;
        this.y = y;
        this.particles = [];
        this.createParticles();
    }

    createParticles() {
        const particleCount = 50 + Math.random() * 50;
        const colors = ['#b90000', '#d10000', '#FF6B6B', '#FFD700', '#FFA500', '#890000', '#c71010'];

        for (let i = 0; i < particleCount; i++) {
            const angle = (Math.PI * 2 * i) / particleCount;
            const velocity = 2 + Math.random() * 4;
            const color = colors[Math.floor(Math.random() * colors.length)];

            this.particles.push({
                x: this.x,
                y: this.y,
                vx: Math.cos(angle) * velocity,
                vy: Math.sin(angle) * velocity,
                life: 1.0,
                decay: 0.01 + Math.random() * 0.02,
                size: 2 + Math.random() * 3,
                color: color
            });
        }
    }

    update() {
        this.particles.forEach(particle => {
            particle.x += particle.vx;
            particle.y += particle.vy;
            particle.vy += 0.1; // gravity
            particle.life -= particle.decay;
        });

        this.particles = this.particles.filter(particle => particle.life > 0);
    }

    draw(ctx) {
        this.particles.forEach(particle => {
            ctx.save();
            ctx.globalAlpha = particle.life;
            ctx.fillStyle = particle.color;
            ctx.beginPath();
            ctx.arc(particle.x, particle.y, particle.size, 0, Math.PI * 2);
            ctx.fill();
            ctx.restore();
        });
    }

    isDead() {
        return this.particles.length === 0;
    }
}

class FireworksDisplay {
    constructor() {
        this.canvas = document.getElementById('fireworksCanvas');
        this.ctx = this.canvas.getContext('2d');
        this.fireworks = [];
        this.animationId = null;
        this.isActive = false;
    }

    start() {
        if (this.isActive) return;

        this.isActive = true;
        this.canvas.classList.add('active');
        this.resize();

        // Create multiple fireworks
        const fireworkCount = 5 + Math.floor(Math.random() * 5);
        for (let i = 0; i < fireworkCount; i++) {
            setTimeout(() => {
                this.createFirework();
            }, i * 200);
        }

        this.animate();

        // Auto-stop after duration
        setTimeout(() => {
            this.stop();
        }, 4000);
    }

    createFirework() {
        const x = Math.random() * this.canvas.width;
        const y = Math.random() * (this.canvas.height * 0.5) + (this.canvas.height * 0.1);
        this.fireworks.push(new Firework(x, y));
    }

    resize() {
        this.canvas.width = window.innerWidth;
        this.canvas.height = window.innerHeight;
    }

    animate() {
        if (!this.isActive) return;

        this.ctx.fillStyle = 'rgba(0, 0, 0, 0.1)';
        this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);

        this.fireworks.forEach(firework => {
            firework.update();
            firework.draw(this.ctx);
        });

        this.fireworks = this.fireworks.filter(firework => !firework.isDead());

        this.animationId = requestAnimationFrame(() => this.animate());
    }

    stop() {
        this.isActive = false;
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
        }
        this.canvas.classList.remove('active');
        this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
        this.fireworks = [];
    }
}

// Global instance
let fireworksDisplay = null;

function showFireworks() {
    if (!fireworksDisplay) {
        fireworksDisplay = new FireworksDisplay();
    }
    fireworksDisplay.start();
}

// Resize handler
window.addEventListener('resize', () => {
    if (fireworksDisplay) {
        fireworksDisplay.resize();
    }
});
